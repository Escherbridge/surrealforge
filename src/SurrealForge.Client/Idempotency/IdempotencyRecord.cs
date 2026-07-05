using System;

namespace SurrealForge.Client.Idempotency
{
    /// <summary>
    /// A persisted idempotency-ledger record, decoupled from any consuming
    /// application's domain model. Plain data — no framework attributes, no
    /// dependencies beyond the BCL. Consumers map this to/from their own
    /// domain types as needed.
    ///
    /// Exactly-once execution of an irreversible operation is enforced by a
    /// UNIQUE constraint on <see cref="Key"/> combined with insert-wins
    /// semantics: the first caller to insert an
    /// <see cref="IdempotencyState.InProgress"/> row "wins" the claim;
    /// concurrent inserts fail the unique constraint and re-read this record.
    /// </summary>
    public sealed class IdempotencyRecord
    {
        /// <summary>
        /// The idempotency key. Caller-supplied (e.g. an Idempotency-Key header)
        /// or a deterministic content hash. Subject to the UNIQUE constraint.
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Logical operation type (e.g. "bridge_redeem", "faucet_dispense").
        /// Aids diagnostics; the uniqueness guarantee is on <see cref="Key"/> alone.
        /// </summary>
        public string OperationType { get; set; } = string.Empty;

        /// <summary>Lifecycle state of the operation.</summary>
        public IdempotencyState State { get; set; } = IdempotencyState.InProgress;

        /// <summary>
        /// Serialized result of the completed operation. Replayed verbatim to
        /// duplicate callers so they observe the same outcome as the original.
        /// </summary>
        public string? ResultPayload { get; set; }

        /// <summary>
        /// Failure reason when <see cref="State"/> is
        /// <see cref="IdempotencyState.Failed"/>.
        /// </summary>
        public string? Error { get; set; }

        /// <summary>Creation timestamp (UTC).</summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Last-update timestamp (UTC).</summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Result of an atomic claim-or-get against the idempotency ledger.
    /// </summary>
    /// <param name="Won">
    /// <c>true</c> if THIS caller inserted the row and therefore owns the right
    /// to execute the irreversible effect exactly once. <c>false</c> if a record
    /// already existed (a duplicate/concurrent request); inspect
    /// <paramref name="Record"/> to replay the prior outcome.
    /// </param>
    /// <param name="Record">
    /// The authoritative record. When <paramref name="Won"/> is <c>true</c> this
    /// is the freshly-inserted <see cref="IdempotencyState.InProgress"/> row.
    /// When <c>false</c> it is the pre-existing record (which may be InProgress,
    /// Completed, or Failed).
    /// </param>
    public sealed record IdempotencyClaim(bool Won, IdempotencyRecord Record);
}
