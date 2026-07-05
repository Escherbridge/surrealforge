namespace SurrealForge.Client.Query
{
    /// <summary>
    /// Sort direction for <see cref="SurrealQuery.OrderBy(string, OrderDirection)"/>.
    ///
    /// Defined as an explicit enum (not a free-form string) so callers cannot
    /// accidentally smuggle a clause-altering token through the <c>OrderBy</c>
    /// builder.
    /// </summary>
    public enum OrderDirection
    {
        /// <summary>Ascending — emits <c>ASC</c>.</summary>
        Asc = 0,

        /// <summary>Descending — emits <c>DESC</c>.</summary>
        Desc = 1,
    }

    /// <summary>
    /// Allowed values for the <c>RETURN</c> clause of a SurrealQL DML statement.
    ///
    /// Defined as an enum (not a string) so the <c>.Return(...)</c> builder
    /// cannot be passed a clause-altering token. The
    /// <see cref="SurrealQuery.Return(ReturnClause)"/> string overload still
    /// exists for legacy callers; it parses the string against this enum and
    /// rejects anything else.
    /// </summary>
    public enum ReturnClause
    {
        /// <summary>Return the record state from BEFORE the mutation.</summary>
        Before = 0,

        /// <summary>Return the record state AFTER the mutation.</summary>
        After = 1,

        /// <summary>Return only the diff between BEFORE and AFTER.</summary>
        Diff = 2,

        /// <summary>Return nothing (the statement still executes).</summary>
        None = 3,
    }
}
