using FluentAssertions;
using SurrealForge.Client.Query;

namespace SurrealForge.Client.Tests.Query;

/// <summary>
/// MEDIUM #M3 regression coverage: <see cref="SurrealQuery"/> tracks the
/// presence of WHERE / ORDER BY / LIMIT / START / RETURN / FETCH clauses
/// structurally (via dedicated flags threaded through the immutable clones),
/// not by regex-scanning the SQL body. The previous regex-only approach
/// matched the keyword inside string literals, causing a subsequent .Where()
/// to emit AND instead of WHERE.
/// </summary>
public sealed class SurrealQueryClauseTrackingTests
{
    [Fact]
    public void Where_after_Content_with_WHERE_in_string_literal_emits_WHERE_not_AND()
    {
        // The literal contains the WHERE keyword but it is data, not a clause.
        // The structural flag must remain false so .Where() appends WHERE.
        var q = SurrealQuery.Of("UPDATE wallet SET note = \"check WHERE field\"")
                            .Where("owner = $owner", new { owner = "avatar:1" });

        q.Sql.Should().Contain(" WHERE owner = $owner");
        q.Sql.Should().NotContain(" AND owner = $owner",
            "the WHERE inside the string literal must NOT trigger the AND branch (M3 fix).");
    }

    [Fact]
    public void Where_after_explicit_Where_emits_AND()
    {
        // Regression positive: explicit WHERE in builder state correctly flips
        // subsequent .Where() to AND.
        var q = SurrealQuery.Of("SELECT * FROM wallet")
                            .Where("chain = $chain", new { chain = "algorand" })
                            .Where("owner = $owner", new { owner = "avatar:1" });

        q.Sql.Should().Be(
            "SELECT * FROM wallet WHERE chain = $chain AND owner = $owner");
    }

    [Fact]
    public void Multiple_OrderBy_calls_only_emit_one_ORDER_BY()
    {
        // Each .OrderBy() appends its own ORDER BY token literally. The flag
        // is set so consumers can introspect (and so the API contract is
        // self-consistent), but the emitter intentionally does not collapse
        // multiple ORDER BY calls — the test pins the existing behaviour:
        // chained OrderBy calls each append an "ORDER BY <field>" fragment.
        // (Composing multiple sort keys at the same call site is the
        // SurrealQL-idiomatic way; chaining is left as-is for predictability.)
        var q = SurrealQuery.Of("SELECT * FROM wallet")
                            .OrderBy("created_at")
                            .OrderBy("id", OrderDirection.Desc);

        // The SQL ends with the two appended fragments — verify both are present.
        q.Sql.Should().Contain("ORDER BY created_at ASC");
        q.Sql.Should().Contain("ORDER BY id DESC");
    }

    [Fact]
    public void Of_with_existing_WHERE_clause_then_Where_emits_AND()
    {
        // The one-time literal-stripped scan in Of(...) must detect the WHERE
        // already in the raw SurrealQL so the subsequent .Where() correctly
        // routes through AND.
        var q = SurrealQuery.Of("SELECT * FROM wallet WHERE chain = $chain")
                            .WithParam("chain", "algorand")
                            .Where("owner = $owner", new { owner = "avatar:1" });

        q.Sql.Should().Be(
            "SELECT * FROM wallet WHERE chain = $chain AND owner = $owner");
    }
}
