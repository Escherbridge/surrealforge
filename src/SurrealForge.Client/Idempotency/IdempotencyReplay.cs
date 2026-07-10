using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SurrealForge.Client.Idempotency
{
    /// <summary>
    /// Application-agnostic idempotency-replay machinery: the deterministic
    /// content-hash key, the JSON round-trip for cached results, and the replay
    /// state machine that turns a persisted <see cref="IdempotencyRecord"/> back
    /// into a caller-shaped result.
    ///
    /// The state machine is generic over the RESULT type <c>T</c>
    /// AND over the caller's result shape <c>TResult</c>: the
    /// consumer supplies a small <see cref="IReplayResultFactory{T, TResult}"/>
    /// (or the delegate-based overload) so this package never depends on any
    /// application's result envelope. This is the "enable replay through config
    /// if needed" seam — a consumer that wants replay wires its own factory;
    /// one that does not simply never calls it.
    /// </summary>
    public static class IdempotencyReplay
    {
        /// <summary>
        /// Deterministic content-hash tail of an idempotency key: SHA-256 over
        /// the already-canonicalised <paramref name="canonical"/> string,
        /// rendered as lowercase hex. Callers build the canonical string (e.g.
        /// <c>string.Join("|", fields…)</c>) so the field projection stays
        /// per-request.
        /// </summary>
        public static string ContentHash(string canonical)
        {
            if (canonical == null) throw new ArgumentNullException(nameof(canonical));

            byte[] hash;
            // Instance API (not SHA256.HashData, which is net5+) so this compiles
            // on netstandard2.0 as well as net10.0.
            using (var sha = SHA256.Create())
            {
                hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            }

            var sb = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
                sb.Append(hash[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>
        /// JSON options used for the replay round-trip. Web defaults.
        /// </summary>
        public static readonly JsonSerializerOptions ReplayJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        /// <summary>Serialize a completed result for caching on the idempotency record.</summary>
        public static string SerializeForReplay<T>(T result)
            => JsonSerializer.Serialize(result, ReplayJson);

        /// <summary>
        /// Deserialize a cached replay payload, swallowing malformed JSON
        /// (returns <c>null</c>).
        /// </summary>
        public static T? DeserializeForReplay<T>(string payload) where T : class
        {
            try { return JsonSerializer.Deserialize<T>(payload, ReplayJson); }
            catch (JsonException) { return null; }
        }

        /// <summary>
        /// The replay state machine, generic over the result type
        /// <typeparamref name="T"/> and the caller's result envelope
        /// <typeparamref name="TResult"/>. Behaviour:
        /// <list type="bullet">
        /// <item><see cref="IdempotencyState.Completed"/> with a payload that
        /// deserializes ⇒ <c>factory.Success</c> carrying the cached result
        /// (after <paramref name="markReplayed"/>); if the payload cannot be
        /// replayed ⇒ <c>factory.Error(replayDeserializeFailedMessage)</c>.</item>
        /// <item><see cref="IdempotencyState.Failed"/> ⇒
        /// <c>factory.Error</c> with the recorded <see cref="IdempotencyRecord.Error"/>,
        /// or <paramref name="originalFailedMessage"/> when none was recorded.</item>
        /// <item>InProgress / Completed-without-payload ⇒
        /// <c>factory.Error(inProgressMessage)</c>.</item>
        /// </list>
        /// The caller supplies the type-specific deserialize, the replayed-flag
        /// mutation, and every message string so the exact wording and result
        /// shape are preserved.
        /// </summary>
        public static TResult ReplayFromRecord<T, TResult>(
            IdempotencyRecord record,
            IReplayResultFactory<T, TResult> factory,
            Func<string, T?> deserialize,
            Action<T> markReplayed,
            string replaySuccessMessage,
            string replayDeserializeFailedMessage,
            string originalFailedMessage,
            string inProgressMessage)
            where T : class
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            switch (record.State)
            {
                case IdempotencyState.Completed when !string.IsNullOrEmpty(record.ResultPayload):
                    var replayed = deserialize(record.ResultPayload!);
                    if (replayed != null)
                    {
                        markReplayed(replayed);
                        return factory.Success(replayed, replaySuccessMessage);
                    }
                    return factory.Error(replayDeserializeFailedMessage);

                case IdempotencyState.Failed:
                    return factory.Error(
                        string.IsNullOrEmpty(record.Error) ? originalFailedMessage : record.Error!);

                default:
                    // InProgress (or Completed with no payload): the original
                    // effect is not yet known to have settled. Do NOT re-execute;
                    // surface a retryable in-progress state.
                    return factory.Error(inProgressMessage);
            }
        }

        /// <summary>
        /// Delegate-based overload of
        /// <see cref="ReplayFromRecord{T, TResult}(IdempotencyRecord, IReplayResultFactory{T, TResult}, Func{string, T}, Action{T}, string, string, string, string)"/>
        /// for consumers that prefer to pass two lambdas instead of implementing
        /// the factory interface.
        /// </summary>
        public static TResult ReplayFromRecord<T, TResult>(
            IdempotencyRecord record,
            Func<T, string, TResult> onSuccess,
            Func<string, TResult> onError,
            Func<string, T?> deserialize,
            Action<T> markReplayed,
            string replaySuccessMessage,
            string replayDeserializeFailedMessage,
            string originalFailedMessage,
            string inProgressMessage)
            where T : class
            => ReplayFromRecord(
                record,
                new DelegateReplayResultFactory<T, TResult>(onSuccess, onError),
                deserialize,
                markReplayed,
                replaySuccessMessage,
                replayDeserializeFailedMessage,
                originalFailedMessage,
                inProgressMessage);

        private sealed class DelegateReplayResultFactory<T, TResult> : IReplayResultFactory<T, TResult>
        {
            private readonly Func<T, string, TResult> _success;
            private readonly Func<string, TResult> _error;

            public DelegateReplayResultFactory(Func<T, string, TResult> success, Func<string, TResult> error)
            {
                _success = success ?? throw new ArgumentNullException(nameof(success));
                _error = error ?? throw new ArgumentNullException(nameof(error));
            }

            public TResult Success(T result, string message) => _success(result, message);
            public TResult Error(string message) => _error(message);
        }
    }

    /// <summary>
    /// Builds a consumer-shaped result from the replay state machine, so the
    /// package stays free of any application's result envelope. Implement this
    /// (or use the delegate overload) to bind replay to your own result type.
    /// </summary>
    /// <typeparam name="T">The replayed result payload type.</typeparam>
    /// <typeparam name="TResult">The caller's result envelope type.</typeparam>
    public interface IReplayResultFactory<in T, out TResult>
    {
        /// <summary>Build a success result carrying the replayed payload.</summary>
        TResult Success(T result, string message);

        /// <summary>Build an error result with the given message.</summary>
        TResult Error(string message);
    }
}
