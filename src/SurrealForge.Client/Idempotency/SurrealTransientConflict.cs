using System;
using System.Threading;
using System.Threading.Tasks;

namespace SurrealForge.Client.Idempotency
{
    /// <summary>
    /// Shared optimistic-concurrency retry primitive for SurrealDB 3.x
    /// single-winner conditional-UPDATE / INSERT-wins seams.
    ///
    /// SurrealDB's RocksDB storage engine raises a transient write-write
    /// contention error ("Transaction conflict: Resource busy ... can be
    /// retried") when concurrent transactions touch the same row. It is a
    /// plain exception (no dedicated type), so the signal is matched on stable
    /// message tokens. On retry the winner's write has already landed, so a
    /// conditional-UPDATE loser resolves cleanly to its no-op / affected==0
    /// path — the loop is safe to wrap contended single-winner claims.
    /// </summary>
    public static class SurrealTransientConflict
    {
        /// <summary>Default bounded retry budget for a contended single-winner claim.</summary>
        public const int DefaultMaxRetries = 8;

        /// <summary>
        /// True when <paramref name="ex"/> is SurrealDB 3.x's transient
        /// write-write contention signal. Matched on message tokens because the
        /// RocksDB engine raises a plain exception type; the tokens are stable
        /// across 3.x.
        /// </summary>
        public static bool IsRetryableConflict(Exception ex)
        {
            if (ex == null) return false;
            var m = ex.Message ?? string.Empty;
            return Contains(m, "Transaction conflict")
                || Contains(m, "Resource busy")
                || Contains(m, "can be retried");
        }

        /// <summary>
        /// Runs <paramref name="operation"/> under a bounded retry loop, retrying
        /// only on <see cref="IsRetryableConflict"/>. Backoff is a small
        /// exponential-ish delay with per-attempt jitter to break the contending
        /// herd. When the budget is exhausted the last conflict exception
        /// propagates.
        /// </summary>
        public static async Task<T> RetryOnConflictAsync<T>(
            Func<Task<T>> operation,
            CancellationToken ct,
            int maxRetries = DefaultMaxRetries)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < maxRetries && IsRetryableConflict(ex))
                {
                    // Random is fine here — jitter only needs to de-correlate the
                    // contending herd, not be cryptographically strong. Random.Shared
                    // is net6+, so use a thread-static instance for netstandard2.0.
                    var jitterMs = Jitter.Next(0, 4);
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(5 * (attempt + 1) + jitterMs), ct)
                        .ConfigureAwait(false);
                }
            }
        }

        // string.Contains(string, StringComparison) is net-only; this compiles
        // on netstandard2.0 too and stays case-insensitive.
        private static bool Contains(string haystack, string needle)
            => haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        [ThreadStatic] private static Random? _jitter;
        private static Random Jitter => _jitter ??= new Random(unchecked(Environment.TickCount * 31 + Environment.CurrentManagedThreadId));
    }
}
