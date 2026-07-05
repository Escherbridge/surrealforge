// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Client.Query -- lightweight unit-of-work change tracking for
// SurrealContext (surreal-linq-graph-query Phase 3, decision D4).
//
// NOT a full EF proxy/relationship-fixup tracker. An identity map keyed on the
// [Id] value + an EntityState per entry. Modified entries are detected by an
// explicit Update() call (snapshot-diff is the natural extension but the
// homebake preference is to keep this minimal: explicit Add/Update/Remove).
// SaveChangesAsync walks the entries and emits one CREATE/UPSERT/DELETE each,
// flushed in a single BEGIN..COMMIT.

#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using SurrealForge.Client.Schema;

namespace SurrealForge.Client.Query
{
    /// <summary>Pending persistence action for a tracked entity.</summary>
    public enum SurrealEntityState
    {
        /// <summary>Loaded/attached, no pending write.</summary>
        Unchanged,
        /// <summary>New — emit CREATE on save.</summary>
        Added,
        /// <summary>Mutated — emit UPSERT (full-content replace) on save.</summary>
        Modified,
        /// <summary>Marked for deletion — emit DELETE on save.</summary>
        Deleted,
    }

    /// <summary>One tracked entity: its instance, table, id, and pending state.</summary>
    public sealed class SurrealEntityEntry
    {
        public object Entity { get; }
        public string Table { get; }
        public string Id { get; }
        public SurrealEntityState State { get; internal set; }

        // Typed statement factories captured at track-time (where T is known),
        // so SaveChanges can emit the coercion-safe SET-based CREATE/UPSERT via
        // SurrealWriter<T> without reflecting T back out of `object`.
        internal Func<SurrealQuery> CreateStatement { get; }
        internal Func<SurrealQuery> UpsertStatement { get; }

        internal SurrealEntityEntry(
            object entity, string table, string id, SurrealEntityState state,
            Func<SurrealQuery> createStatement, Func<SurrealQuery> upsertStatement)
        {
            Entity = entity;
            Table = table;
            Id = id;
            State = state;
            CreateStatement = createStatement;
            UpsertStatement = upsertStatement;
        }

        internal (string table, string id) Key => (Table, Id);
    }

    /// <summary>
    /// Identity map + pending-state registry. Keyed on (table, id) so the same
    /// record tracked twice resolves to one entry (dedup). Reflection reads the
    /// <c>[Id]</c>-marked property once per type (cached).
    /// </summary>
    public sealed class SurrealChangeTracker
    {
        private readonly Dictionary<(string, string), SurrealEntityEntry> _entries = new();
        private static readonly Dictionary<Type, PropertyInfo> _idPropCache = new();
        private static readonly object _cacheLock = new();

        public IReadOnlyCollection<SurrealEntityEntry> Entries => _entries.Values;

        /// <summary>Mark <paramref name="entity"/> for INSERT (CREATE).</summary>
        public SurrealEntityEntry Add<T>(T entity) where T : ISurrealRecord, new()
            => Track<T>(entity!, SurrealEntityState.Added);

        /// <summary>Mark <paramref name="entity"/> for UPSERT (full-content update).</summary>
        public SurrealEntityEntry Update<T>(T entity) where T : ISurrealRecord, new()
            => Track<T>(entity!, SurrealEntityState.Modified);

        /// <summary>Mark <paramref name="entity"/> for DELETE.</summary>
        public SurrealEntityEntry Remove<T>(T entity) where T : ISurrealRecord, new()
            => Track<T>(entity!, SurrealEntityState.Deleted);

        /// <summary>Track <paramref name="entity"/> as Unchanged (identity-map registration).</summary>
        public SurrealEntityEntry Attach<T>(T entity) where T : ISurrealRecord, new()
            => Track<T>(entity!, SurrealEntityState.Unchanged);

        /// <summary>Drop all tracked entries (called after a successful save).</summary>
        public void Clear() => _entries.Clear();

        private SurrealEntityEntry Track<T>(object entity, SurrealEntityState state)
            where T : ISurrealRecord, new()
        {
            if (entity is null) throw new ArgumentNullException(nameof(entity));
            var table = RecordId<T>.SchemaNameOf<T>();
            var id = ReadId(entity);
            if (string.IsNullOrEmpty(id))
                throw new InvalidOperationException(
                    $"Cannot track a {typeof(T).Name} with an empty [Id] — set the id before Add/Update/Remove.");

            var key = (table, id);
            if (_entries.TryGetValue(key, out var existing))
            {
                // Identity-map dedup: the latest intent wins (e.g. Add then
                // Remove on the same id collapses to Remove).
                existing.State = state;
                return existing;
            }
            var typed = (T)entity;
            var entry = new SurrealEntityEntry(
                entity, table, id, state,
                createStatement: () => SurrealWriter.Create(typed),
                upsertStatement: () => SurrealWriter.Upsert(typed));
            _entries[key] = entry;
            return entry;
        }

        private static string ReadId(object entity)
        {
            var type = entity.GetType();
            PropertyInfo? idProp;
            lock (_cacheLock)
            {
                if (!_idPropCache.TryGetValue(type, out idProp))
                {
                    idProp = FindIdProperty(type);
                    _idPropCache[type] = idProp!;
                }
            }
            if (idProp is null)
                throw new InvalidOperationException(
                    $"{type.Name} has no [Id]-marked property; SurrealContext change tracking requires one.");
            return idProp.GetValue(entity)?.ToString() ?? string.Empty;
        }

        private static PropertyInfo? FindIdProperty(Type type)
        {
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetCustomAttribute<IdAttribute>(inherit: true) != null)
                    return p;
            }
            // Fallback: a property literally named "Id".
            return type.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
        }
    }
}
