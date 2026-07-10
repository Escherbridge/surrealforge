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
        private readonly IdempotencyLedgerOptions _options;

        /// <summary>
        /// Creates a ledger over the given executor and table with default
        /// options (no retry, no key encoding).
        /// </summary>
        /// <param name="executor">The SurrealDB executor used for all queries.</param>
        /// <param name="table">
        /// The ledger table name. Defaults to <see cref="DefaultTable"/>
        /// (<c>idempotency_key_store</c>).
        /// </param>
        public SurrealIdempotencyLedger(ISurrealExecutor executor, string table = DefaultTable)
            : this(executor, new IdempotencyLedgerOptions { Table = table })
        {
        }

        /// <summary>
        /// Creates a ledger over the given executor, configured by
        /// <paramref name="options"/>. This is the config-driven entry point:
        /// transient-conflict retry and colon-key encoding are turned on and
        /// tuned here (see <see cref="IdempotencyLedgerOptions"/>).
        /// </summary>
        public SurrealIdempotencyLedger(ISurrealExecutor executor, IdempotencyLedgerOptions options)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.Validate();
            _options = options;
            _table = options.Table;
        }

        // ── TryClaimAsync ──────────────────────────────────────────────────────

        /// <summary>
        /// Atomically claim the key or return the existing record. Inserts a new
        /// <see cref="IdempotencyState.InProgress"/> row when the key is unseen
        /// (returns <c>Won=true</c>); on a unique-constraint violation
        /// (concurrent/duplicate request) re-reads and returns <c>Won=false</c>
        /// with the existing record.
        /// </summary>
        public Task<IdempotencyClaim> TryClaimAsync(
            string key,
            string operationType,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Idempotency key must be non-empty.", nameof(key));

            // Config-gated: wrap the claim in the bounded transient-conflict
            // retry loop only when the consumer opted in. The default is the
            // plain single-attempt claim.
            if (_options.RetryOnTransientConflict)
            {
                return SurrealTransientConflict.RetryOnConflictAsync(
                    () => TryClaimOnceAsync(key, operationType, ct),
                    ct,
                    _options.MaxConflictRetries);
            }

            return TryClaimOnceAsync(key, operationType, ct);
        }

        private async Task<IdempotencyClaim> TryClaimOnceAsync(
            string key,
            string operationType,
            CancellationToken ct)
        {
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
            var content = BuildContentDict(recordId, StoreKey(key), operationType, now);
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

        /// <summary>Maps the internal row POCO to the public package record,
        /// restoring the original key when colon-key encoding is enabled.</summary>
        private IdempotencyRecord ToRecord(IdempotencyKeyRow r)
        {
            return new IdempotencyRecord
            {
                Key           = RestoreKey(r.Key),
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

        // ── Colon-key encoding (config-gated) ────────────────────────────────
        //
        // The deterministic record id is always SHA-256 of the ORIGINAL key, so
        // encoding only rewrites the value stored in the `key` column. A raw
        // colon is the SurrealDB record-id separator and trips up query/id
        // ergonomics, so keys containing ':' are stored base64url-encoded under
        // an unambiguous prefix and restored on read. Keys without a colon (and
        // the whole feature when disabled) pass through verbatim.

        /// <summary>Encodes a key for storage when colon-key encoding is enabled
        /// and the key contains a colon; otherwise returns it unchanged.</summary>
        private string StoreKey(string key)
        {
            if (!_options.EncodeColonKeys) return key;
            if (key.IndexOf(':') < 0) return key;

            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(key))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            return _options.EncodedKeyPrefix + payload;
        }

        /// <summary>Inverse of <see cref="StoreKey"/>: restores the original key
        /// from a stored value. Non-encoded values pass through; malformed
        /// payloads are returned verbatim rather than throwing.</summary>
        private string RestoreKey(string stored)
        {
            if (!_options.EncodeColonKeys) return stored;
            var prefix = _options.EncodedKeyPrefix;
            if (string.IsNullOrEmpty(prefix) ||
                !stored.StartsWith(prefix, StringComparison.Ordinal))
                return stored;

            try
            {
                var payload = stored.Substring(prefix.Length)
                    .Replace('-', '+')
                    .Replace('_', '/');
                var padding = (4 - payload.Length % 4) % 4;
                if (padding > 0) payload = payload.PadRight(payload.Length + padding, '=');
                return Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            }
            catch (FormatException)
            {
                return stored;
            }
        }
    }
}
