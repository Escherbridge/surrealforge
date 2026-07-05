using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;
using Xunit;

namespace SurrealForge.Analyzer.Tests;

/// <summary>
/// Verifies that <c>SRDB0001</c> fires for unsafe SurrealQL construction and
/// passes for safe parameterized queries.
///
/// Each test uses the Roslyn analyzer testing framework to compile a small
/// C# snippet in-process and assert the expected diagnostics.
///
/// Snippets that reference SurrealQuery include an inline stub of that type so
/// no external assembly reference is needed (avoids System.Runtime version
/// mismatch CS1705 between consumers and the harness reference assemblies).
/// </summary>
public sealed class SurrealQlSafetyAnalyzerTests
{
    // Minimal stub of SurrealQuery inserted at the top of each test snippet that needs it.
    // The analyzer only checks syntax/names — not the actual type implementation.
    // Stub lives in the new SurrealForge.Client.Query allowlist namespace; that
    // is also the namespace consumers `using` to import the real builder.
    private const string SurrealQueryStub = """
        namespace SurrealForge.Client.Query
        {
            public sealed class SurrealQuery
            {
                public static SurrealQuery Of(string sql) => new SurrealQuery();
                public SurrealQuery WithParam(string key, object? value) => this;
            }
        }
        """;

    private static Task RunAsync(string source, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<SurrealQlSafetyAnalyzerDiagnostic, XUnitVerifier>
        {
            TestCode = source,
        };
        if (expected.Length > 0)
            test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    // Variant that prepends the SurrealQuery stub as a second file in the compilation.
    private static Task RunWithSurrealQueryAsync(string source, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<SurrealQlSafetyAnalyzerDiagnostic, XUnitVerifier>
        {
            TestCode = source,
        };
        test.TestState.Sources.Add(SurrealQueryStub);
        if (expected.Length > 0)
            test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    // ─── PASS cases (no SRDB0001) ────────────────────────────────────────────

    [Fact]
    public async Task Pass_SurrealQuery_Of_with_literal_and_WithParam()
    {
        // Correct pattern: compile-time literal + named param
        const string source = """
            using SurrealForge.Client.Query;
            using System;

            class TestClass
            {
                void Test(string avatarId)
                {
                    var q = SurrealQuery.Of("SELECT * FROM wallet WHERE owner = $owner")
                                        .WithParam("owner", avatarId);
                }
            }
            """;

        // No diagnostics expected
        await RunWithSurrealQueryAsync(source);
    }

    [Fact]
    public async Task Pass_const_string_in_Of()
    {
        const string source = """
            using SurrealForge.Client.Query;

            class TestClass
            {
                private const string Sql = "SELECT * FROM wallet WHERE id = $id";

                void Test(object id)
                {
                    var q = SurrealQuery.Of(Sql).WithParam("id", id);
                }
            }
            """;

        await RunWithSurrealQueryAsync(source);
    }

    [Fact]
    public async Task Pass_static_readonly_string_is_allowed()
    {
        // Static readonly is not a compile-time const but it IS a simple
        // identifier resolving to a field, not a local — one-hop resolution
        // only follows locals, so it stays a pass.
        const string source = """
            using SurrealForge.Client.Query;

            class TestClass
            {
                private static readonly string Sql = "SELECT * FROM wallet";

                void Test()
                {
                    var q = SurrealQuery.Of(Sql);
                }
            }
            """;

        await RunWithSurrealQueryAsync(source);
    }

    // ─── FAIL cases (SRDB0001 expected) ──────────────────────────────────────

    [Fact]
    public async Task Fail_interpolated_string_in_SurrealQuery_Of()
    {
        const string source = """
            using SurrealForge.Client.Query;
            using System;

            class TestClass
            {
                void Test(string id)
                {
                    var q = SurrealQuery.Of({|SRDB0001:$"SELECT * FROM wallet WHERE id = {id}"|});
                }
            }
            """;

        await RunWithSurrealQueryAsync(source);
    }

    [Fact]
    public async Task Fail_string_concatenation_in_SurrealQuery_Of()
    {
        const string source = """
            using SurrealForge.Client.Query;

            class TestClass
            {
                void Test(string id)
                {
                    var q = SurrealQuery.Of({|SRDB0001:"SELECT * FROM wallet WHERE id = " + id|});
                }
            }
            """;

        await RunWithSurrealQueryAsync(source);
    }

    [Fact]
    public async Task Fail_interpolated_string_passed_to_db_Query()
    {
        // Interface named IFakeSurrealDb ends with "SurrealDb" — the analyzer's
        // IsSurrealDbTypeName heuristic matches it even via the semantic path.
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using System.Collections.Generic;

            interface IFakeSurrealDb
            {
                Task<object> Query(string sql, IReadOnlyDictionary<string, object?> parms, CancellationToken ct = default);
            }

            class TestClass
            {
                private readonly IFakeSurrealDb _client = null!;

                async Task Test(string id)
                {
                    var result = await _client.Query({|SRDB0001:$"SELECT * FROM wallet WHERE id = {id}"|}, new Dictionary<string, object?>());
                }
            }
            """;

        await RunAsync(source);
    }

    [Fact]
    public async Task Fail_string_concatenation_passed_to_db_RawQuery()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using System.Collections.Generic;

            interface IFakeSurrealDb
            {
                Task<object> RawQuery(string sql, IReadOnlyDictionary<string, object?> parms, CancellationToken ct = default);
            }

            class TestClass
            {
                private readonly IFakeSurrealDb _client = null!;

                async Task Test(string table)
                {
                    var result = await _client.RawQuery({|SRDB0001:"SELECT * FROM " + table|}, new Dictionary<string, object?>());
                }
            }
            """;

        await RunAsync(source);
    }

    [Fact]
    public async Task Fail_string_Format_in_SurrealQuery_Of()
    {
        const string source = """
            using SurrealForge.Client.Query;
            using System;

            class TestClass
            {
                void Test(string table)
                {
                    var q = SurrealQuery.Of({|SRDB0001:string.Format("SELECT * FROM {0}", table)|});
                }
            }
            """;

        await RunWithSurrealQueryAsync(source);
    }

    // ─── Allowlist: safe layer is exempt ─────────────────────────────────────

    [Fact]
    public async Task Pass_code_inside_safe_query_layer_is_exempt()
    {
        // Code in the SurrealForge.Client.Query namespace must NOT be flagged
        // even if it uses string interpolation — that IS the safe construction
        // layer itself. The stub already lives in this namespace.
        const string source = """
            namespace SurrealForge.Client.Query
            {
                class InternalHelper
                {
                    void Build(string table)
                    {
                        // This is inside the allowlist namespace — not flagged
                        var q = SurrealQuery.Of($"SELECT * FROM {table}");
                    }
                }
            }
            """;

        // No diagnostics expected (allowlist)
        await RunWithSurrealQueryAsync(source);
    }

    [Fact]
    public async Task Pass_legacy_Core_SurrealDb_Query_namespace_is_exempt()
    {
        // The legacy YourApp Core.SurrealDb.Query namespace must remain
        // allowlisted while the YourApp ProjectReference still points at
        // the legacy types (A5 deletes it).
        const string source = """
            namespace YourApp.Core.SurrealDb.Query
            {
                public sealed class SurrealQuery
                {
                    public static SurrealQuery Of(string sql) => new SurrealQuery();
                }
                class InternalHelper
                {
                    void Build(string table)
                    {
                        var q = SurrealQuery.Of($"SELECT * FROM {table}");
                    }
                }
            }
            """;

        await RunAsync(source);
    }

    // ─── One-hop variable resolution (H3 bypass closure) ────────────────────

    [Fact]
    public async Task Fail_one_hop_local_string_concat_initializer_in_SurrealQuery_Of()
    {
        // The H3 bypass scenario from code review: an unsafe string-concat is
        // hidden behind a single local variable. The analyzer now follows the
        // local declaration one hop and reports at the SurrealQuery.Of call site.
        const string source = """
            using SurrealForge.Client.Query;

            class TestClass
            {
                void Test(string userInput)
                {
                    var sql = "SELECT * FROM " + userInput;
                    var q = SurrealQuery.Of({|SRDB0001:sql|});
                }
            }
            """;

        await RunWithSurrealQueryAsync(source);
    }

    [Fact]
    public async Task Fail_one_hop_local_interpolated_initializer_in_SurrealQuery_Of()
    {
        const string source = """
            using SurrealForge.Client.Query;

            class TestClass
            {
                void Test(string table)
                {
                    var sql = $"SELECT * FROM {table}";
                    var q = SurrealQuery.Of({|SRDB0001:sql|});
                }
            }
            """;

        await RunWithSurrealQueryAsync(source);
    }

    [Fact]
    public async Task Fail_one_hop_local_string_format_initializer_in_SurrealQuery_Of()
    {
        const string source = """
            using SurrealForge.Client.Query;
            using System;

            class TestClass
            {
                void Test(string table)
                {
                    var sql = string.Format("SELECT * FROM {0}", table);
                    var q = SurrealQuery.Of({|SRDB0001:sql|});
                }
            }
            """;

        await RunWithSurrealQueryAsync(source);
    }

    [Fact]
    public async Task Fail_one_hop_local_passed_to_db_Query()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using System.Collections.Generic;

            interface IFakeSurrealDb
            {
                Task<object> Query(string sql, IReadOnlyDictionary<string, object?> parms, CancellationToken ct = default);
            }

            class TestClass
            {
                private readonly IFakeSurrealDb _client = null!;

                async Task Test(string id)
                {
                    var sql = $"SELECT * FROM wallet WHERE id = {id}";
                    var result = await _client.Query({|SRDB0001:sql|}, new Dictionary<string, object?>());
                }
            }
            """;

        await RunAsync(source);
    }

    [Fact]
    public async Task Pass_one_hop_local_with_safe_literal_initializer()
    {
        // One-hop *negative*: a literal-constant initializer is safe and the
        // analyzer must not report at the call site.
        const string source = """
            using SurrealForge.Client.Query;

            class TestClass
            {
                void Test()
                {
                    var sql = "SELECT * FROM literal_table";
                    var q = SurrealQuery.Of(sql);
                }
            }
            """;

        await RunWithSurrealQueryAsync(source);
    }

    // ─── HIGH#5 — semantic resolution of SurrealQuery.Of via the symbol model ───
    //
    // The legacy `IsSurrealQueryType` did `memberAccess.Expression.ToString() == "SurrealQuery"`
    // which silently missed fully-qualified and aliased call shapes — a clean
    // bypass for anyone willing to type a few extra characters. Fix #5 plumbs
    // the SemanticModel through so the analyzer resolves the receiver to its
    // IMethodSymbol and checks the containing type's full display string.

    [Fact]
    public async Task Fail_fully_qualified_SurrealQuery_Of_with_concat_initializer()
    {
        // Note: NO `using SurrealForge.Client.Query;` — the call is fully-
        // qualified. The pre-fix analyzer compared the receiver token literally
        // ("global::SurrealForge.Client.Query.SurrealQuery" != "SurrealQuery")
        // and let this slip through.
        const string source = """
            class TestClass
            {
                void Test(string userInput)
                {
                    var sql = "SELECT * FROM wallet WHERE id = " + userInput;
                    var q = global::SurrealForge.Client.Query.SurrealQuery.Of({|SRDB0001:sql|});
                }
            }
            """;

        await RunWithSurrealQueryAsync(source);
    }

    [Fact]
    public async Task Fail_aliased_SurrealQuery_Of_with_interpolated_initializer()
    {
        // `using SQ = ...` aliasing — same bypass shape as fully-qualified,
        // different syntactic surface.
        const string source = """
            using SQ = SurrealForge.Client.Query.SurrealQuery;

            class TestClass
            {
                void Test(string userInput)
                {
                    var q = SQ.Of({|SRDB0001:$"SELECT * FROM wallet WHERE id = {userInput}"|});
                }
            }
            """;

        await RunWithSurrealQueryAsync(source);
    }

    [Fact]
    public async Task Pass_fully_qualified_SurrealQuery_Of_with_literal_is_not_flagged()
    {
        // Negative case for the semantic-resolution path: a literal SurrealQL
        // string is safe regardless of whether the call is short, qualified,
        // or aliased.
        const string source = """
            class TestClass
            {
                void Test()
                {
                    var q = global::SurrealForge.Client.Query.SurrealQuery
                                    .Of("SELECT * FROM wallet WHERE id = $id");
                }
            }
            """;

        await RunWithSurrealQueryAsync(source);
    }
}
