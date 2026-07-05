using System;

namespace SurrealForge.Client.Query
{
    /// <summary>
    /// Thrown when a runtime-provided SurrealQL identifier (table name or
    /// record-id suffix) fails validation: empty, illegal characters, or — newly
    /// closing code-review H4 — a SurrealQL reserved word.
    ///
    /// The exception is distinct from <see cref="ArgumentException"/> so callers
    /// can catch identifier-policy failures separately from generic argument bugs.
    /// </summary>
    public sealed class SurrealIdentifierException : ArgumentException
    {
        public SurrealIdentifierException(string message)
            : base(message) { }

        public SurrealIdentifierException(string message, string paramName)
            : base(message, paramName) { }
    }
}
