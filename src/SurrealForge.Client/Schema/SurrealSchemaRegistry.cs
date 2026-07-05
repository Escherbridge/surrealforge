// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Client -- centralized Type -> SurrealDB table-name lookup.
//
// Design intent (added 2026-06-07):
//   Schema-name metadata is config-level, NOT data-level. POCOs declare
//   their table name via the `[SurrealTable("...")]` attribute (a build-
//   time fact about the type) and queries / record-ids / typed builders
//   look up the name through this registry. The legacy
//   `ISurrealRecord.SchemaName` instance property still works (the
//   registry falls back to `new T().SchemaName` for types that don't
//   carry the attribute, e.g. the inline adapter POCOs in
//   Providers/Stores/Surreal/*.cs), but new POCOs are encouraged to drop
//   the property and rely on the registry alone.
//
//   The registry is read-mostly + write-once-per-type: first call for a
//   given Type reflects/computes the table name and caches it; subsequent
//   calls return the cached string. Thread-safe via
//   ConcurrentDictionary.
//
//   Explicit registration is also supported -- the DI host or test
//   harness can pre-seed the lookup via Register<T>("table") so the
//   first runtime call is a hit instead of a miss.

using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace SurrealForge.Client.Schema
{
    /// <summary>
    /// Process-wide registry mapping a POCO CLR <see cref="Type"/> to its
    /// SurrealDB table name. Auto-populates from
    /// <see cref="SurrealTableAttribute"/> when present; falls back to
    /// <see cref="ISurrealRecord.SchemaName"/> for types that don't carry
    /// the attribute. Pre-seed explicitly via
    /// <see cref="Register{T}(string)"/> at DI startup if you want to skip
    /// the first-call reflection cost.
    /// </summary>
    public static class SurrealSchemaRegistry
    {
        private static readonly ConcurrentDictionary<Type, string> Cache =
            new ConcurrentDictionary<Type, string>();

        /// <summary>
        /// Return the SurrealDB table name for <typeparamref name="T"/>.
        /// Cached after the first call. Resolution order:
        /// <list type="number">
        /// <item>Explicit registration via <see cref="Register{T}(string)"/></item>
        /// <item><see cref="SurrealTableAttribute"/> on the type</item>
        /// <item><see cref="ISurrealRecord.SchemaName"/> via parameterless ctor</item>
        /// </list>
        /// Throws <see cref="InvalidOperationException"/> when none of the
        /// three resolution paths apply.
        /// </summary>
        public static string For<T>() where T : ISurrealRecord
            => For(typeof(T));

        /// <summary>
        /// Non-generic counterpart of <see cref="For{T}"/> for code paths
        /// that only have a <see cref="Type"/> handle.
        /// </summary>
        public static string For(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            return Cache.GetOrAdd(type, ResolveTableName);
        }

        /// <summary>
        /// Pre-register a table name for <typeparamref name="T"/>. The first
        /// call wins; subsequent <c>Register</c> calls for the same type
        /// throw rather than silently swap the cached value (drift is a bug,
        /// not a feature).
        /// </summary>
        public static void Register<T>(string tableName) where T : ISurrealRecord
            => Register(typeof(T), tableName);

        /// <summary>Non-generic <see cref="Register{T}(string)"/>.</summary>
        public static void Register(Type type, string tableName)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (string.IsNullOrWhiteSpace(tableName))
                throw new ArgumentException("Table name must not be empty.", nameof(tableName));

            if (!Cache.TryAdd(type, tableName))
            {
                if (Cache.TryGetValue(type, out var existing) && existing == tableName)
                {
                    // Idempotent repeat-register with the same value -- treat as no-op.
                    return;
                }
                throw new InvalidOperationException(
                    $"Surreal schema for '{type.FullName}' was already registered " +
                    $"as '{Cache[type]}'; refusing to overwrite with '{tableName}'.");
            }
        }

        /// <summary>
        /// Test-only escape hatch: clear all cached entries. Useful in
        /// unit tests that rotate fixture POCOs through the same Type
        /// keys across runs. Not exposed for production code paths.
        /// </summary>
        public static void ClearCacheForTesting() => Cache.Clear();

        private static string ResolveTableName(Type type)
        {
            // 1. [SurrealTable("...")] attribute is the canonical declaration.
            var attr = type.GetCustomAttribute<SurrealTableAttribute>(inherit: false);
            if (attr != null && !string.IsNullOrWhiteSpace(attr.Name))
            {
                return attr.Name;
            }

            // 2. Fallback: instantiate and read ISurrealRecord.SchemaName.
            //    Used by hand-rolled inline POCOs in store adapters that
            //    don't carry the attribute. The activator path is wrapped
            //    so failures surface a descriptive error pointing the
            //    developer at the right fix.
            if (typeof(ISurrealRecord).IsAssignableFrom(type))
            {
                try
                {
                    var instance = (ISurrealRecord)Activator.CreateInstance(type)!;
                    var name = instance.SchemaName;
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
                catch (MissingMethodException ex)
                {
                    throw new InvalidOperationException(
                        $"SurrealSchemaRegistry could not resolve a table name for " +
                        $"'{type.FullName}': the type has no [SurrealTable] attribute " +
                        $"and no parameterless constructor for the ISurrealRecord.SchemaName " +
                        $"fallback. Either add [SurrealTable(\"<name>\")] or call " +
                        $"SurrealSchemaRegistry.Register<{type.Name}>(\"<name>\") at startup.",
                        ex);
                }
            }

            throw new InvalidOperationException(
                $"SurrealSchemaRegistry could not resolve a table name for " +
                $"'{type.FullName}'. Add [SurrealTable(\"<name>\")] to the type, " +
                $"implement ISurrealRecord with a non-empty SchemaName, or call " +
                $"SurrealSchemaRegistry.Register<{type.Name}>(\"<name>\") at startup.");
        }
    }
}
