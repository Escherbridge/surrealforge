using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SurrealForge.Client.Query
{
    /// <summary>
    /// Query-layer extensions to the transport-defined
    /// <see cref="SurrealStatementResult"/> (Phase 2 / A1).  Adds the
    /// affected-row semantics that the G2 conditional-state-transition
    /// primitive relies on without forcing those concepts into the transport
    /// type.
    ///
    /// Closes code-review C5 use-case: callers can assert exactly-one-row
    /// affected for the conditional-update primitive, rather than relying on
    /// an aggregate count smuggled through the legacy <c>int</c> return.
    /// </summary>
    public static class SurrealStatementResultExtensions
    {
        /// <summary>
        /// Number of records affected by this statement, derived from the
        /// <see cref="SurrealStatementResult.Result"/> JSON payload:
        /// <list type="bullet">
        ///   <item>Array → array length.</item>
        ///   <item>Object → 1.</item>
        ///   <item>Null / undefined → 0.</item>
        ///   <item>Scalar (number/string/bool) → 1.</item>
        /// </list>
        ///
        /// SurrealDB does not surface an explicit affected-row count on the
        /// wire; this is the standard inference used by the executor layer.
        /// </summary>
        public static int AffectedCount(this SurrealStatementResult statement)
        {
            if (statement is null) throw new ArgumentNullException(nameof(statement));
            if (!statement.IsOk) return 0;

            var r = statement.Result;
            switch (r.ValueKind)
            {
                case JsonValueKind.Array:     return r.GetArrayLength();
                case JsonValueKind.Object:    return 1;
                case JsonValueKind.Null:      return 0;
                case JsonValueKind.Undefined: return 0;
                default:                      return 1; // scalar
            }
        }

        /// <summary>
        /// Returns the single affected row deserialized as
        /// <typeparamref name="T"/>, asserting exactly one record was
        /// affected.
        ///
        /// This is the read-side companion to
        /// <see cref="SurrealQuery.UpdateOnly(string, string)"/> — the G2
        /// conditional-state-transition primitive guarantees at-most-one
        /// affected row, and this helper enforces at-least-one too.
        ///
        /// Throws <see cref="InvalidOperationException"/> when the statement
        /// affected zero or more than one row, or when the statement is in
        /// the ERR state.
        /// </summary>
        public static T EnsureSingleAffected<T>(
            this SurrealStatementResult statement,
            JsonSerializerOptions? options = null)
        {
            if (statement is null) throw new ArgumentNullException(nameof(statement));
            if (!statement.IsOk)
                throw new InvalidOperationException(
                    "Cannot read affected row from a statement that failed: " +
                    (SurrealResponse.ExtractErrorText(statement) ?? "(no detail)"));

            IReadOnlyList<T> values = statement.GetValues<T>(options);
            if (values.Count == 0)
                throw new InvalidOperationException(
                    "Expected exactly one affected row but the statement affected zero. " +
                    "This typically means the WHERE clause did not match — the conditional " +
                    "state transition was rejected.");
            if (values.Count > 1)
                throw new InvalidOperationException(
                    "Expected exactly one affected row but the statement affected " +
                    values.Count + ". The WHERE clause is not selective enough to be a " +
                    "single-record conditional transition.");
            return values[0];
        }
    }
}
