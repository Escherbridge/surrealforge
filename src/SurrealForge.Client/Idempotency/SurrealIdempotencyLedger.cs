using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SurrealForge.Client.Query;

namespace SurrealForge.Client.Idempotency
{
    /// <summary>
    /// Generic, application-agnostic exactly-once execution ledger backed by a
    /// SurrealDB table. Any consumer (not just a particular WebAPI) can use it
    /// to get claim / complete / fail / get semantics without re-implementing
    /// the SurrealDB calls.
    ///
    /// Atomicity model:
    ///   The ledger table is expected to carry a UNIQUE index on the
    ///   <c>key</c> field. <see cref="TryClaimAsync"/> attempts to INSERT a
    ///   fresh InProgress row via a <c>CREATE</c> statement. When SurrealDB
    ///   rejects the INSERT due to the UNIQUE violation the per-statement HTTP
    ///   slot returns <c>status="ERR"</c>; this is detected by inspecting
    ///   <see cref="SurrealStatementResult.IsOk"/> on <c>response[0]</c> and the
    ///   error text via <see cref="SurrealStatementResult.ErrorText"/>. The
    ///   CREATE deliberately does NOT call
    ///   <see cref="SurrealResponse.EnsureAllOk"/> — the duplicate path is the
    ///   EXPECTED race-loss and must be handled gracefully, not thrown.
    ///
    /// Record-id encoding (deterministic):
    ///   The SurrealDB record id is derived from the caller-supplied key:
    ///   SHA-256(UTF-8(key)) → 64-char lowercase hex. The output is safe for
    ///   SurrealDB record ids (only [0-9a-f]) and makes every read an O(1)
    ///   record-id lookup, allowing the conditional UPDATE to address the row
    ///   without a preceding SELECT.
    ///
    /// State-transition guard:
    ///   <see cref="CompleteAsync"/> / <see cref="FailAsync"/> use a multi-field
    ///   conditional UPDATE that only fires when <c>state = "InProgress"</c>.
    ///   Zero affected rows → the claim was already resolved (race-lost); the
    ///   method is a no-op.
    /// </summary>
    public sealed class SurrealIdempotencyLedger
    {
        /// <summary>Default ledger table name.</summary>
        public const string DefaultTable = "idempotency_key_store";

        // On-wire string literals for the constrained `state` column.
        private const string StateInProgress = "InProgress";
        private const string StateCompleted  = "Completed";
        private const string StateFailed     = "Failed";

        private readonly ISurrealExecutor _executor;
        private readonly string _table;

        /// <summary>
        /// Creates a ledger over the given executor and table.
        /// </summary>
        /// <param name="executor">The SurrealDB executor used for all queries.</param>
        /// <param name="table">
        /// The ledger table name. Defaults to <see cref="DefaultTable"/>
        /// (<c>idempotency_key_store</c>).
        /// </param>
        public SurrealIdempotencyLedger(ISurrealExecutor executor, string table = DefaultTable)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            if (string.IsNullOrWhiteSpace(table))
                throw new ArgumentException("Ledger table name must be non-empty.", nameof(table));
            _table = table;
        }

        // ── TryClaimAsync ──────────────────────────────────────────────────────

        /// <summary>
        /// Atomically claim the key or return the existing record. Inserts a new
        /// <see cref="IdempotencyState.InProgress"/> row when the key is unseen
        /// (returns <c>Won=true</c>); on a unique-constraint violation
        /// (concurrent/duplicate request) re-reads and returns <c>Won=false</c>
        /// with the existing record.
        /// </summary>
        public async Task<IdempotencyClaim> TryClaimAsync(
            string key,
            string operationType,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Idempotency key must be non-empty.", nameof(key));

            var recordId = DeterministicId(key);
            var now      = DateTimeOffset.UtcNow;

            // Fast-path: if the record already exists, return it without
            // attempting an INSERT. CancellationToken.None is intentional — a
            // duplicate must still replay, never surface a raw cancellation.
            var existing = await FetchByRecordIdAsync(recordId, CancellationToken.None).ConfigureAwait(false);
            if (existing != null)
                return new IdempotencyClaim(false, ToRecord(existing));

            // Attempt INSERT-wins. SurrealDB rejects a duplicate on the UNIQUE
            // index on `key` with status="ERR" in the per-statement slot.
            // We use ExecuteAsync (not QueryAsync) so the ERR is surfaced as
            // response[0].IsOk == false rather than thrown.
            var content = BuildContentDict(recordId, key, operationType, now);
            var insertQ = SurrealQuery
                .Of("CREATE type::record($_t, $_id) CONTENT $_content RETURN AFTER")
                .WithParam("_t",       _table)
                .WithParam("_id",      recordId)
                .WithParam("_content", content);

            // NB: do NOT call response.EnsureAllOk() here — a duplicate INSERT is
            // the EXPECTED race-loss path and returns status="ERR" in response[0].
            // We inspect response[0].IsOk per-statement (below) and treat the
            // unique / record-already-exists ERR as "someone else won".
            var response = await _executor.ExecuteAsync(insertQ, ct).ConfigureAwait(false);

            if (response[0].IsOk)
            {
                // INSERT succeeded — this caller wins the claim.
                var inserted = response[0].GetValues<IdempotencyKeyRow>();
                var row = inserted.Count > 0 ? inserted[0] : null;

                // If RETURN AFTER gave us the row, use it; otherwise construct a
                // synthetic record from what we sent.
                var won = row != null
                    ? ToRecord(row)
                    : new IdempotencyRecord
                    {
                        Key           = key,
                        OperationType = operationType,
                        State         = IdempotencyState.InProgress,
                        CreatedAt     = now.UtcDateTime,
                        UpdatedAt     = now.UtcDateTime
                    };
                return new IdempotencyClaim(true, won);
            }

            // The INSERT was rejected — positively confirm it was a UNIQUE
            // violation (or deterministic record-id collision) by inspecting the
            // error text. Use ErrorText, not Detail: 3.x puts the failed-statement
            // message in the `result` slot, leaving Detail null.
            var detail = response[0].ErrorText ?? string.Empty;
            if (!IsUniqueViolation(detail))
            {
                // Genuine error (not a UNIQUE collision) — surface it.
                throw new InvalidOperationException(
                    "SurrealIdempotencyLedger.TryClaimAsync failed for key '" + key + "': " +
                    "SurrealDB returned ERR: " + detail);
            }

            // UNIQUE violation: re-read the winning row. CancellationToken.None —
            // same rationale as the fast-path read above.
            var winner = await FetchByRecordIdAsync(recordId, CancellationToken.None).ConfigureAwait(false);
            if (winner != null)
                return new IdempotencyClaim(false, ToRecord(winner));

            // UNIQUE violation but the winning row vanished (concurrent delete).
            // Surface the original error rather than fabricating a claim.
            throw new InvalidOperationException(
                "SurrealIdempotencyLedger.TryClaimAsync: UNIQUE violation for key '" + key + "' " +
                "but the winning row was not found on re-read. Original detail: " + detail);
        }

        // ── CompleteAsync ──────────────────────────────────────────────────────

        /// <summary>
        /// Mark a claimed key as <see cref="IdempotencyState.Completed"/> and
        /// cache the serialized result for replay to duplicate callers.
        /// No-op when the row is not in the InProgress state (already terminal).
        /// </summary>
        public async Task CompleteAsync(string key, string resultPayload, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Idempotency key must be non-empty.", nameof(key));

            var recordId = DeterministicId(key);

            // Conditional multi-field UPDATE: only fires when state = InProgress.
            // All values are bound as $params — no interpolation.
            var q = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) SET state = $_next, result_payload = $_payload, updated_at = $_now WHERE state = $_expected RETURN AFTER")
                .WithParam("_t",        _table)
                .WithParam("_id",       recordId)
                .WithParam("_expected", StateInProgress)
                .WithParam("_next",     StateCompleted)
                .WithParam("_payload",  resultPayload)
                .WithParam("_now",      DateTimeOffset.UtcNow);

            var response = await _executor.ExecuteAsync(q, ct).ConfigureAwait(false);
            response.EnsureAllOk();

            if (!response[0].IsOk)
            {
                var detail = response[0].ErrorText ?? string.Empty;
                throw new InvalidOperationException(
                    "Cannot complete idempotency key '" + key + "': " +
                    "SurrealDB returned ERR: " + detail + ". " +
                    "CompleteAsync must follow a winning TryClaimAsync.");
            }

            // Zero affected rows → state was not InProgress (already Completed or
            // Failed). No-op by design (caller lost the race or is re-calling).
        }

        // ── FailAsync ──────────────────────────────────────────────────────────

        /// <summary>
        /// Mark a claimed key as <see cref="IdempotencyState.Failed"/> with the
        /// given error. No-op when the row is not in the InProgress state.
        /// </summary>
        public async Task FailAsync(string key, string error, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Idempotency key must be non-empty.", nameof(key));

            var recordId = DeterministicId(key);

            var q = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) SET state = $_next, error = $_error, updated_at = $_now WHERE state = $_expected RETURN AFTER")
                .WithParam("_t",        _table)
                .WithParam("_id",       recordId)
                .WithParam("_expected", StateInProgress)
                .WithParam("_next",     StateFailed)
                .WithParam("_error",    error)
                .WithParam("_now",      DateTimeOffset.UtcNow);

            var response = await _executor.ExecuteAsync(q, ct).ConfigureAwait(false);
            response.EnsureAllOk();

            if (!response[0].IsOk)
            {
                var detail = response[0].ErrorText ?? string.Empty;
                throw new InvalidOperationException(
                    "Cannot fail idempotency key '" + key + "': " +
                    "SurrealDB returned ERR: " + detail + ". " +
                    "FailAsync must follow a winning TryClaimAsync.");
            }

            // Zero affected rows → not InProgress. No-op by design.
        }

        // ── GetAsync ───────────────────────────────────────────────────────────

        /// <summary>
        /// Fetch the record for a key, or <c>null</c> if the key has never been
        /// claimed.
        /// </summary>
        public async Task<IdempotencyRecord?> GetAsync(string key, CancellationToken ct)
        {
            var recordId = DeterministicId(key);
            var row = await FetchByRecordIdAsync(recordId, ct).ConfigureAwait(false);
            return row != null ? ToRecord(row) : null;
        }

        // ── Private helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Derives the SurrealDB record id from an idempotency key.
        ///
        /// Encoding: SHA-256(UTF-8(key)) → 64-char lowercase hex string. Safe for
        /// SurrealDB record ids (only [0-9a-f]). Deterministic: same key always
        /// produces the same id, enabling O(1) record-id lookups.
        /// </summary>
        public static string DeterministicId(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            byte[] hash;
            // SHA256.HashData is net5+; use the instance API so this compiles on
            // netstandard2.0 as well as net8.0.
            using (var sha = SHA256.Create())
            {
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
            }

            var sb = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
                sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>
        /// Fetches a row by its deterministic record id. Returns null when not
        /// found.
        /// </summary>
        private async Task<IdempotencyKeyRow?> FetchByRecordIdAsync(string recordId, CancellationToken ct)
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::record($_t, $_id)")
                .WithParam("_t",  _table)
                .WithParam("_id", recordId);

            var response = await _executor.ExecuteAsync(q, ct).ConfigureAwait(false);
            response.EnsureAllOk();

            if (!response[0].IsOk)
                return null;

            var values = response[0].GetValues<IdempotencyKeyRow>();
            return values.Count > 0 ? values[0] : null;
        }

        /// <summary>
        /// Detects a SurrealDB UNIQUE-index violation from the statement error
        /// text. SurrealDB surfaces a message containing the index name (e.g.
        /// <c>"idempotency_key_unique"</c>) or the words "Unique" / "duplicate" /
        /// "already exists" / "index".
        ///
        /// Positive-identification check — if the detail does NOT match any of
        /// these patterns the caller rethrows the original error rather than
        /// masking it as an idempotent replay.
        /// </summary>
        private static bool IsUniqueViolation(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return false;

            return ContainsOrdinalIgnoreCase(detail, "idempotency_key_unique")
                || ContainsOrdinalIgnoreCase(detail, "Unique")
                || ContainsOrdinalIgnoreCase(detail, "duplicate")
                || ContainsOrdinalIgnoreCase(detail, "already exists")
                || ContainsOrdinalIgnoreCase(detail, "index");
        }

        // string.Contains(string, StringComparison) is net-only; this helper
        // compiles on netstandard2.0 too.
        private static bool ContainsOrdinalIgnoreCase(string haystack, string needle)
        {
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Builds the content dictionary for the INSERT / CREATE statement.
        /// Uses explicit string keys matching the SurrealDB schema field names.
        ///
        /// option&lt;T&gt; columns (result_payload / error / ttl_expires_at) are
        /// OMITTED rather than set to null: SurrealDB 3.x rejects an explicit JSON
        /// null on an option&lt;T&gt; field — an absent field is the NONE the
        /// schema wants.
        /// </summary>
        private static Dictionary<string, object?> BuildContentDict(
            string recordId,
            string key,
            string operationType,
            DateTimeOffset now)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"]             = recordId,
                ["key"]            = key,
                ["operation_type"] = operationType,
                ["state"]          = StateInProgress,
                ["created_at"]     = now,
                ["updated_at"]     = now,
            };
        }

        /// <summary>Maps the internal row POCO to the public package record.</summary>
        private static IdempotencyRecord ToRecord(IdempotencyKeyRow r)
        {
            return new IdempotencyRecord
            {
                Key           = r.Key,
                OperationType = r.OperationType,
                State         = ParseState(r.State),
                ResultPayload = r.ResultPayload,
                Error         = r.Error,
                CreatedAt     = r.CreatedAt.UtcDateTime,
                UpdatedAt     = r.UpdatedAt.UtcDateTime,
            };
        }

        private static IdempotencyState ParseState(string state)
        {
            if (string.Equals(state, StateCompleted, StringComparison.Ordinal))
                return IdempotencyState.Completed;
            if (string.Equals(state, StateFailed, StringComparison.Ordinal))
                return IdempotencyState.Failed;
            return IdempotencyState.InProgress;
        }
    }
}
