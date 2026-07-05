using System;
using System.Text.Json.Serialization;

namespace SurrealForge.Client.Idempotency
{
    /// <summary>
    /// Internal SurrealDB row shape for the idempotency-ledger table. Mirrors
    /// the columns of the <c>idempotency_key_store</c> schema (id, key,
    /// operation_type, state, result_payload, error, created_at, updated_at,
    /// ttl_expires_at).
    ///
    /// This is the package's OWN deserialization target — the ledger does not
    /// depend on any consumer's generated POCO. The <c>state</c> column is a
    /// constrained string ("InProgress" | "Completed" | "Failed") on the wire;
    /// it is mapped to/from <see cref="IdempotencyState"/> in the ledger.
    /// </summary>
    internal sealed class IdempotencyKeyRow
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("operation_type")]
        public string OperationType { get; set; } = string.Empty;

        // The schema constrains this to "InProgress" | "Completed" | "Failed".
        // We read it as the raw string and translate to IdempotencyState so the
        // ledger is resilient to any future literal the schema might add.
        [JsonPropertyName("state")]
        public string State { get; set; } = "InProgress";

        [JsonPropertyName("result_payload")]
        public string? ResultPayload { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }

        [JsonPropertyName("ttl_expires_at")]
        public DateTimeOffset? TtlExpiresAt { get; set; }
    }
}
