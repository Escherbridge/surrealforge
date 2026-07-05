namespace SurrealForge.Client.Idempotency
{
    /// <summary>
    /// Lifecycle state of an idempotent operation tracked by
    /// <see cref="SurrealIdempotencyLedger"/>.
    ///
    /// The string forms ("InProgress", "Completed", "Failed") are the on-wire
    /// values stored in the SurrealDB <c>state</c> column (the schema constrains
    /// the column to exactly these three literals).
    /// </summary>
    public enum IdempotencyState
    {
        /// <summary>The claim has been won and the irreversible effect is being executed.</summary>
        InProgress,

        /// <summary>The irreversible effect completed successfully; the result payload is cached for replay.</summary>
        Completed,

        /// <summary>The irreversible effect failed; the error reason is recorded.</summary>
        Failed
    }
}
