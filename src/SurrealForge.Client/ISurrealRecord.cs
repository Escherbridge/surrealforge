// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Client -- marker interface implemented by every SurrealDB POCO.
// The POCOs are hand-authored under Persistence/SurrealDb/Models/ and decorated
// with [SurrealTable]; the AttributeSchemaScanner in SurrealForge.Schema
// reflects over them to emit .surql DDL.
//
// The interface exposes the SurrealDB table name as an instance property
// (netstandard2.0 cannot host `static abstract` members on interfaces).
// `RecordId<T>` and `SurrealQuery<T>` rely on this marker + a runtime
// SchemaName lookup to bind their type parameter to a concrete table name.
//
// 2026-06: a config-level SchemaNameRegistry (see SurrealSchemaRegistry)
// now lets POCOs that carry `[SurrealTable]` skip implementing this
// property entirely -- the registry reflects the attribute on first
// access and caches the lookup. The instance property survives for back-
// compat with hand-authored adapter POCOs that don't carry the attribute
// (e.g. the inline shapes in Providers/Stores/Surreal/*.cs). At serialize
// time the SurrealJsonOptions modifier strips this property regardless of
// source, so the table name is never accidentally sent over the wire.

namespace SurrealForge.Client
{
    /// <summary>
    /// Marker contract implemented by every generated SurrealDB POCO. The
    /// <see cref="SchemaName"/> instance property reflects the SurrealDB
    /// table name the POCO maps to; the type-system pin used by
    /// <see cref="RecordId{T}"/> and <c>SurrealQuery&lt;T&gt;</c> defers the
    /// table-name lookup to a single <c>new T()</c> instantiation cached at
    /// type-resolution time.
    /// </summary>
    /// <remarks>
    /// Why an instance property instead of a static one: netstandard2.0
    /// (the target framework for the SurrealForge suite and any
    /// downstream package consumer) does not support C# 11's
    /// <c>static abstract</c> interface members. A cached
    /// <c>new T().SchemaName</c> lookup keyed by <see cref="System.Type"/>
    /// preserves the same zero-runtime-cost shape after first call.
    /// </remarks>
    public interface ISurrealRecord
    {
        /// <summary>
        /// The SurrealDB table this record type maps to (e.g. <c>"wallet"</c>).
        /// Implementations typically return a string constant. POCOs that
        /// also carry the <c>[SurrealTable]</c> attribute can return the
        /// same value (or skip this property entirely if the registry is
        /// the only consumer -- the
        /// <c>SurrealSchemaRegistry.For&lt;T&gt;()</c> lookup reflects the
        /// attribute when the property is absent).
        /// </summary>
        string SchemaName { get; }
    }
}
