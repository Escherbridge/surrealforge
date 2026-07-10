using System;

namespace SurrealForge.Client.Idempotency
{
    /// <summary>
    /// Turn-on/tune-here configuration for <see cref="SurrealIdempotencyLedger"/>.
    /// JSON-config friendly (bool / int / string) so consumers can bind from
    /// <c>appsettings.json</c>, e.g. under <c>SurrealDb:Idempotency</c>:
    /// <code>
    /// services.Configure&lt;IdempotencyLedgerOptions&gt;(
    ///     config.GetSection("SurrealDb:Idempotency"));
    /// </code>
    /// Every knob is off-by-default-safe: the defaults reproduce the plain
    /// ledger behaviour (no retry, no key rewriting) so enabling a feature is a
    /// deliberate opt-in.
    /// </summary>
    public sealed class IdempotencyLedgerOptions
    {
        /// <summary>
        /// Canonical config sub-section name, nested under the SurrealForge
        /// <c>SurrealDb</c> root (i.e. <c>SurrealDb:Idempotency</c>).
        /// </summary>
        public const string ConfigSectionName = "Idempotency";

        /// <summary>Ledger table name. Default <c>idempotency_key_store</c>.</summary>
        public string Table { get; set; } = SurrealIdempotencyLedger.DefaultTable;

        /// <summary>
        /// Retry the claim on SurrealDB 3.x transient write-write conflicts
        /// (RocksDB "Transaction conflict: Resource busy ... can be retried").
        /// When <c>true</c> the claim is wrapped in
        /// <see cref="SurrealTransientConflict.RetryOnConflictAsync{T}"/>.
        /// Default <c>false</c>. Enable on contended single-winner claim paths.
        /// </summary>
        public bool RetryOnTransientConflict { get; set; }

        /// <summary>
        /// Bounded retry budget when <see cref="RetryOnTransientConflict"/> is
        /// enabled. Default <see cref="SurrealTransientConflict.DefaultMaxRetries"/> (8).
        /// </summary>
        public int MaxConflictRetries { get; set; } = SurrealTransientConflict.DefaultMaxRetries;

        /// <summary>
        /// Base64url-encode idempotency keys that contain a colon before they
        /// are stored in the <c>key</c> column. SurrealDB record-id / query
        /// ergonomics can trip over a raw colon (it is the record-id separator),
        /// so a key such as <c>op_bridge:redeem:123</c> is stored encoded and
        /// transparently decoded on read.
        ///
        /// The stored form is prefixed with <see cref="EncodedKeyPrefix"/> so the
        /// decode is unambiguous. The deterministic record id (SHA-256 of the
        /// ORIGINAL key) is unaffected — encoding only touches the stored
        /// <c>key</c> column value.
        ///
        /// Default <c>false</c> (store keys verbatim).
        /// </summary>
        public bool EncodeColonKeys { get; set; }

        /// <summary>
        /// Marker prefix used for base64url-encoded stored keys when
        /// <see cref="EncodeColonKeys"/> is enabled. Overridable so a consumer
        /// with an existing corpus can keep its historical prefix. Must be a
        /// value that cannot occur at the start of a real key.
        /// </summary>
        public string EncodedKeyPrefix { get; set; } = "__sf_idem_b64__";

        /// <summary>Throws when the option set is internally inconsistent.</summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(Table))
                throw new ArgumentException("Idempotency ledger Table must be non-empty.", nameof(Table));
            if (MaxConflictRetries < 0)
                throw new ArgumentException("MaxConflictRetries must be >= 0.", nameof(MaxConflictRetries));
            if (EncodeColonKeys && string.IsNullOrEmpty(EncodedKeyPrefix))
                throw new ArgumentException("EncodedKeyPrefix must be non-empty when EncodeColonKeys is enabled.", nameof(EncodedKeyPrefix));
        }
    }
}
