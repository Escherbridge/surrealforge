// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Client -- typed RecordId<T> companion to the existing
// untyped RecordId. Phantom-type parameter T pins the SurrealDB table at
// compile time, eliminating the entire class of "passed a wallet:abc id
// into a field that expects idempotency_key_store:..." bugs that the
// untyped RecordId could not catch.
//
// Implementation notes:
//   * `T : ISurrealRecord, new()` -- the `new()` constraint lets us cache a
//     single instance of T to read its SchemaName. netstandard2.0 cannot
//     express `static abstract string SchemaName { get; }` so we instantiate
//     once-per-type and cache the result.
//   * Implicit conversion `RecordId<T> -> RecordId` is always safe (widening).
//   * Explicit conversion `RecordId -> RecordId<T>` validates the source
//     RecordId.Table against T's cached SchemaName; mismatch throws
//     InvalidCastException with both names in the message.
//   * Equality and ToString delegate to the wrapped RecordId.

#nullable enable

using System;
using SurrealForge.Client.Json;
using SurrealForge.Client.Schema;

namespace SurrealForge.Client
{
    /// <summary>
    /// Typed wrapper around <see cref="RecordId"/>. The type parameter
    /// <typeparamref name="T"/> pins the SurrealDB table the record id belongs
    /// to; assignment between mismatched tables fails at compile time
    /// (different generic instantiations) rather than at insert.
    /// </summary>
    /// <typeparam name="T">A generated POCO implementing <see cref="ISurrealRecord"/>.</typeparam>
    public readonly struct RecordId<T> : IEquatable<RecordId<T>>
        where T : ISurrealRecord, new()
    {
        private readonly RecordId _inner;

        /// <summary>The SurrealDB table this typed record id belongs to.</summary>
        public string Table => _inner.Table;

        /// <summary>The opaque record-id part (UUID / ULID / numeric / etc.).</summary>
        public string Id => _inner.Id;

        /// <summary>
        /// Construct from raw <paramref name="id"/>. The table is read from
        /// <typeparamref name="T"/>'s <see cref="ISurrealRecord.SchemaName"/>
        /// via a cached <c>new T()</c> instantiation.
        /// </summary>
        public RecordId(string id)
        {
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("Id must not be empty.", nameof(id));
            _inner = new RecordId(SchemaNameOf<T>(), id);
        }

        /// <summary>
        /// Construct from a pre-built <see cref="RecordId"/>. The supplied
        /// record id's <see cref="RecordId.Table"/> must match
        /// <typeparamref name="T"/>'s schema name; mismatch raises
        /// <see cref="InvalidCastException"/>.
        /// </summary>
        public RecordId(RecordId inner)
        {
            var expectedTable = SchemaNameOf<T>();
            if (!string.Equals(inner.Table, expectedTable, StringComparison.Ordinal))
            {
                throw new InvalidCastException(
                    "Cannot wrap RecordId with table '" + inner.Table +
                    "' as RecordId<" + typeof(T).Name + "> (expected table '" + expectedTable + "').");
            }
            _inner = inner;
        }

        /// <summary>Render the SurrealDB string form <c>table:id</c>.</summary>
        public override string ToString() => _inner.ToString();

        /// <summary>Convert back to the untyped <see cref="RecordId"/>.</summary>
        public RecordId AsUntyped() => _inner;

        /// <summary>
        /// Implicit widening conversion to the untyped <see cref="RecordId"/>.
        /// Always safe -- the typed wrapper is a strict pin of the untyped form.
        /// </summary>
        public static implicit operator RecordId(RecordId<T> typed) => typed._inner;

        /// <summary>
        /// Explicit narrowing conversion from an untyped <see cref="RecordId"/>.
        /// Throws <see cref="InvalidCastException"/> if the source record id's
        /// table does not match <typeparamref name="T"/>'s schema name.
        /// </summary>
        public static explicit operator RecordId<T>(RecordId untyped) => new RecordId<T>(untyped);

        public bool Equals(RecordId<T> other) => _inner.Equals(other._inner);
        public override bool Equals(object? obj) => obj is RecordId<T> other && Equals(other);
        public override int GetHashCode() => _inner.GetHashCode();
        public static bool operator ==(RecordId<T> left, RecordId<T> right) => left.Equals(right);
        public static bool operator !=(RecordId<T> left, RecordId<T> right) => !left.Equals(right);

        // ─── Per-T schema-name cache ─────────────────────────────────────────

        /// <summary>
        /// Lookup the SurrealDB schema name for the supplied generated POCO
        /// type. Delegates to <see cref="SurrealSchemaRegistry.For{T}"/>
        /// (which prefers <c>[SurrealTable]</c>, falls back to the
        /// <c>ISurrealRecord.SchemaName</c> instance property). Cached
        /// inside the registry; the per-T cache that used to live here is
        /// retired so the registry is the single source of truth.
        /// </summary>
        public static string SchemaNameOf<TRecord>() where TRecord : ISurrealRecord, new()
            => SurrealSchemaRegistry.For<TRecord>();
    }
}
