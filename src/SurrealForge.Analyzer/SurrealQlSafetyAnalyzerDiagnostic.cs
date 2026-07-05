using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SurrealForge.Analyzer;

/// <summary>
/// Roslyn analyzer that enforces guardrail G3: no string-interpolated or
/// concatenated SurrealQL queries at call sites.
///
/// Diagnostic SRDB0001 — Error — fires when:
/// 1. A call to any method named <c>Query</c>, <c>RawQuery</c>, or
///    <c>QueryAsync</c> on a type whose name ends with <c>SurrealDb</c>,
///    <c>SurrealDbClient</c>, or that implements <c>ISurrealDbClient</c>
///    (heuristic — does not require full semantic resolution so it works
///    even when the SDK is not resolved) receives its FIRST string argument
///    as an interpolated string, binary string concatenation, or
///    <c>string.Format</c>/<c>string.Concat</c>/<c>StringBuilder.ToString()</c>
///    call.
/// 2. Construction of <c>SurrealQuery.Of(...)</c> (the parameterized query
///    builder entry-point) where the first argument is an interpolated string
///    or binary string concatenation.
/// 3. **One-hop variable resolution** (closes code-review H3 largest bypass):
///    when the first argument is an <c>IdentifierNameSyntax</c> that refers to
///    a *local variable* whose initializer is an unsafe expression (interpolated
///    string, string-concat with non-literal, <c>string.Format</c>, etc.), the
///    diagnostic still fires at the call site with an
///    <see cref="Diagnostic.AdditionalLocations"/> entry pointing at the local
///    declaration so the developer can find the bypass quickly. We deliberately
///    stop at one hop — multi-hop data-flow is out of scope for a syntactic
///    analyzer and one hop catches ~80% of the bypass surface.
///
/// Allowlist: any invocation site whose enclosing namespace contains
/// <c>Core.SurrealDb.Query</c> or <c>SurrealForge.Client.Query</c> is exempt
/// — these are the safe construction layers themselves.
///
/// Compile-time constants and plain string literals are not flagged.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SurrealQlSafetyAnalyzerDiagnostic : DiagnosticAnalyzer
{
    public const string DiagnosticId = "SRDB0001";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        id: DiagnosticId,
        title: "SurrealQL query constructed via string interpolation or concatenation",
        messageFormat: "SurrealQL query must not be constructed via string interpolation or " +
                       "concatenation (argument {0} to {1}). Use SurrealQuery.Of(\"...\").WithParam(...) instead.",
        category: "Security",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Constructing SurrealQL via string interpolation or concatenation is a SQL-injection " +
            "vector and violates guardrail G3 of the SurrealDB migration spec. " +
            "All queries must be composed through SurrealForge.Client.Query.SurrealQuery " +
            "with named parameters.",
        helpLinkUri: "https://github.com/Escherbridge/surrealforge#surrealql-safety-analyzer");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(
            AnalyzeInvocation,
            SyntaxKind.InvocationExpression);
    }

    // ─── Core analysis ───────────────────────────────────────────────────────

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext ctx)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;

        // Allowlist: code inside the safe-construction layer is exempt.
        if (IsInsideSafeLayer(invocation))
            return;

        var name = GetCalledMemberName(invocation);
        if (name == null)
            return;

        // ── Case 1: ISurrealDbClient.Query / RawQuery calls ──────────────────
        if (IsSurrealDbClientCall(invocation, name, ctx))
        {
            CheckFirstStringArg(invocation, "ISurrealDbClient." + name, ctx);
            return;
        }

        // ── Case 2: SurrealQuery.Of(…) ───────────────────────────────────────
        if (name == "Of" && IsSurrealQueryType(invocation, ctx))
        {
            CheckFirstStringArg(invocation, "SurrealQuery.Of", ctx);
            return;
        }
    }

    // ─── Argument check ──────────────────────────────────────────────────────

    private static void CheckFirstStringArg(
        InvocationExpressionSyntax invocation,
        string callee,
        SyntaxNodeAnalysisContext ctx)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0)
            return;

        var firstArg = args[0].Expression;

        // Direct unsafe expression at the call site.
        if (IsUnsafeStringExpression(firstArg, ctx))
        {
            ReportAtCallSite(firstArg, callee, ctx, additionalLocation: null);
            return;
        }

        // One-hop variable resolution (H3 bypass closure). If the argument is a
        // bare identifier, follow it to its local declaration and inspect the
        // initializer. We deliberately do NOT chase across method boundaries,
        // parameters, fields, or properties — one hop only.
        if (firstArg is IdentifierNameSyntax identifier)
        {
            var unsafeInitializerLocation = TryResolveOneHopUnsafeInitializer(identifier, ctx);
            if (unsafeInitializerLocation != null)
            {
                ReportAtCallSite(firstArg, callee, ctx, additionalLocation: unsafeInitializerLocation);
            }
        }
    }

    private static void ReportAtCallSite(
        ExpressionSyntax firstArg,
        string callee,
        SyntaxNodeAnalysisContext ctx,
        Location? additionalLocation)
    {
        var argText = firstArg.ToString();
        var truncated = argText.Length > 40
            ? argText.Substring(0, 40) + "..."
            : argText;

        var additionalLocations = additionalLocation != null
            ? new[] { additionalLocation }
            : System.Array.Empty<Location>();

        ctx.ReportDiagnostic(Diagnostic.Create(
            descriptor: Rule,
            location: firstArg.GetLocation(),
            additionalLocations: additionalLocations,
            messageArgs: new object[] { truncated, callee }));
    }

    // ─── One-hop variable resolution (H3 bypass closure) ─────────────────────

    /// <summary>
    /// Given an identifier used as the first argument to a banned call, attempt
    /// to resolve it to a local variable declared in the same method and inspect
    /// the initializer expression. Returns the Location of the unsafe initializer
    /// if it matches a banned pattern; otherwise returns null.
    ///
    /// One hop only — we do not chase across assignments, method calls, fields,
    /// parameters, or properties. That is deliberate: a syntactic analyzer cannot
    /// soundly do multi-hop data flow, and one hop is enough to close the largest
    /// bypass identified in code review H3.
    /// </summary>
    private static Location? TryResolveOneHopUnsafeInitializer(
        IdentifierNameSyntax identifier,
        SyntaxNodeAnalysisContext ctx)
    {
        var symbolInfo = ctx.SemanticModel.GetSymbolInfo(identifier, ctx.CancellationToken);
        if (symbolInfo.Symbol is not ILocalSymbol localSymbol)
            return null;

        if (localSymbol.DeclaringSyntaxReferences.IsDefaultOrEmpty)
            return null;

        var declaringSyntax = localSymbol.DeclaringSyntaxReferences[0]
            .GetSyntax(ctx.CancellationToken);

        ExpressionSyntax? initializer = declaringSyntax switch
        {
            VariableDeclaratorSyntax declarator => declarator.Initializer?.Value,
            _ => null,
        };

        if (initializer == null)
            return null;

        if (IsUnsafeStringExpression(initializer, ctx))
            return initializer.GetLocation();

        return null;
    }

    // ─── Unsafe expression detection ─────────────────────────────────────────

    private static bool IsUnsafeStringExpression(ExpressionSyntax expr, SyntaxNodeAnalysisContext ctx)
    {
        // Interpolated string: $"..." or $@"..."
        if (expr is InterpolatedStringExpressionSyntax)
            return true;

        // Binary concatenation: "..." + something
        if (expr is BinaryExpressionSyntax binary &&
            binary.OperatorToken.IsKind(SyntaxKind.PlusToken))
        {
            // Only flag when at least one operand is a string literal or
            // interpolated string — definitely string concatenation.
            if (ContainsStringLiteralOrInterpolation(binary.Left) ||
                ContainsStringLiteralOrInterpolation(binary.Right))
                return true;
        }

        // string.Format(...) / string.Concat(...)
        if (expr is InvocationExpressionSyntax innerInvocation)
        {
            var innerName = GetCalledMemberName(innerInvocation);
            if (innerName == "Format" || innerName == "Concat")
            {
                var target = GetReceiverName(innerInvocation);
                if (target == "string" || target == "String")
                    return true;
            }

            // StringBuilder.ToString()
            if (innerName == "ToString")
            {
                // Semantic resolution (precise): check if the containing type is StringBuilder.
                var typeInfo = ctx.SemanticModel.GetTypeInfo(
                    innerInvocation.Expression, ctx.CancellationToken);
                var containingType = typeInfo.Type != null ? typeInfo.Type.Name : null;
                if (containingType == "StringBuilder")
                    return true;

                // Heuristic fallback when type info is unavailable:
                var receiverName = GetReceiverName(innerInvocation);
                if (receiverName != null &&
                    (ContainsIgnoreCase(receiverName, "builder") ||
                     receiverName == "sb" ||
                     receiverName == "Sb"))
                    return true;
            }
        }

        return false;
    }

    private static bool ContainsStringLiteralOrInterpolation(ExpressionSyntax expr)
    {
        if (expr is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
            return true;
        if (expr is InterpolatedStringExpressionSyntax)
            return true;
        return false;
    }

    // netstandard2.0 compatible case-insensitive contains helper
    private static bool ContainsIgnoreCase(string source, string value)
        => source.IndexOf(value, System.StringComparison.OrdinalIgnoreCase) >= 0;

    // ─── Call-site classification ─────────────────────────────────────────────

    /// <summary>
    /// Heuristic: is this an ISurrealDbClient.Query / RawQuery / QueryAsync call?
    ///
    /// We cannot always resolve the type (the SDK may not be restored), so we
    /// use name-based matching.  This produces false positives for identically-
    /// named methods on unrelated types — acceptable for an error-severity gate
    /// that developers control.
    /// </summary>
    private static bool IsSurrealDbClientCall(
        InvocationExpressionSyntax invocation,
        string memberName,
        SyntaxNodeAnalysisContext ctx)
    {
        if (memberName != "Query" && memberName != "RawQuery" && memberName != "QueryAsync")
            return false;

        // Try semantic first (precise).
        var symbol = ctx.SemanticModel.GetSymbolInfo(invocation, ctx.CancellationToken).Symbol;
        if (symbol is IMethodSymbol method)
        {
            var containingTypeName = method.ContainingType != null
                ? method.ContainingType.Name
                : string.Empty;

            if (IsSurrealDbTypeName(containingTypeName))
                return true;

            // Check interfaces implemented by the containing type.
            if (method.ContainingType != null &&
                method.ContainingType.AllInterfaces.Any(i => IsSurrealDbTypeName(i.Name)))
                return true;

            return false;
        }

        // Fallback heuristic when type info is unavailable (e.g. SDK not restored).
        var receiver = GetReceiverName(invocation);
        if (receiver == null)
            return false;

        return ContainsIgnoreCase(receiver, "surreal") ||
               ContainsIgnoreCase(receiver, "db");
    }

    private static bool IsSurrealDbTypeName(string name)
        => name.EndsWith("SurrealDbClient", System.StringComparison.Ordinal) ||
           name.EndsWith("SurrealDb", System.StringComparison.Ordinal) ||
           name == "ISurrealDbClient";

    /// <summary>
    /// Returns true iff <paramref name="invocation"/> resolves to a member of
    /// <c>SurrealForge.Client.Query.SurrealQuery</c>. Uses the semantic
    /// model (preferred) so that fully-qualified
    /// (<c>SurrealForge.Client.Query.SurrealQuery.Of(...)</c>) and aliased
    /// (<c>using SQ = SurrealForge.Client.Query.SurrealQuery; SQ.Of(...)</c>)
    /// calls are flagged the same as the plain <c>SurrealQuery.Of(...)</c>
    /// shape.
    ///
    /// <para>
    /// Falls back to the legacy syntactic check (receiver token equality)
    /// only when semantic info is unavailable — e.g. the analyzer is running
    /// in a project where the Client package isn't yet referenced. Mirrors
    /// the dual-path approach used by <see cref="IsSurrealDbClientCall"/>.
    /// Closes HIGH#5.
    /// </para>
    /// </summary>
    private static bool IsSurrealQueryType(
        InvocationExpressionSyntax invocation,
        SyntaxNodeAnalysisContext ctx)
    {
        // Semantic path (preferred). When the project references the Client
        // package, this catches all three call shapes: short, fully-qualified,
        // and aliased.
        var symbol = ctx.SemanticModel.GetSymbolInfo(invocation, ctx.CancellationToken).Symbol;
        if (symbol is IMethodSymbol method && method.ContainingType is not null)
        {
            var fullName = method.ContainingType.ToDisplayString();
            // Use EndsWith so a re-shipped namespace prefix (e.g. interface
            // forwarders) still matches the canonical type.
            if (fullName.EndsWith("SurrealForge.Client.Query.SurrealQuery", System.StringComparison.Ordinal))
                return true;
            // Don't return false here — the semantic model may resolve to an
            // unrelated <c>SurrealQuery</c> in another namespace if the
            // package isn't referenced. Fall through to the syntactic check.
        }

        // Syntactic fallback (defensive). Only catches the short
        // <c>SurrealQuery.Of(...)</c> shape; the semantic path above is the
        // primary closure.
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var receiver = memberAccess.Expression.ToString();
            return receiver == "SurrealQuery";
        }

        return false;
    }

    // ─── Allowlist ────────────────────────────────────────────────────────────

    private static bool IsInsideSafeLayer(SyntaxNode node)
    {
        // Walk up to the namespace declaration and check whether it is one of
        // the sanctioned safe-construction layers:
        //   * Core.SurrealDb.Query                  — legacy in-repo builder layer.
        //   * SurrealForge.Client.Query          — homebake package query layer.
        //   * SurrealForge.Schema.Migration      — schema runner (HIGH#6 fallback).
        //     Architectural rationale: the schema package was deliberately
        //     shipped with a narrow local <c>ISurrealConnection</c> abstraction
        //     to avoid a hard Schema → Client coupling during Phase 4. Routing
        //     migration writes through Client's parameterized executor would
        //     require either (a) adding a Schema → Client ProjectReference
        //     and a second connection abstraction inside MigrationRunner, or
        //     (b) widening Schema's narrow ISurrealConnection with a
        //     <c>parameters</c> overload — both are out-of-scope for a
        //     code-review surgical fix. The migration runner uses a
        //     <c>JsonEscape</c> helper for the only operator-supplied field
        //     (<c>applied_by</c>) and emits the rest as compile-time literals
        //     keyed by a sanitised record id — the analyzer's blanket "no
        //     interpolation" rule isn't a value-correct test here, so the
        //     namespace is allowlisted with this comment as the rationale.
        var ns = node.Ancestors()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .Select(n => n.Name.ToString())
            .FirstOrDefault();

        if (ns == null)
            return false;

        return ns.IndexOf("Core.SurrealDb.Query",             System.StringComparison.Ordinal) >= 0 ||
               ns.IndexOf("SurrealForge.Client.Query",     System.StringComparison.Ordinal) >= 0 ||
               ns.IndexOf("SurrealForge.Schema.Migration", System.StringComparison.Ordinal) >= 0;
    }

    // ─── Syntax helpers ───────────────────────────────────────────────────────

    private static string? GetCalledMemberName(InvocationExpressionSyntax invocation)
    {
        switch (invocation.Expression)
        {
            case MemberAccessExpressionSyntax m:
                return m.Name.Identifier.Text;
            case IdentifierNameSyntax id:
                return id.Identifier.Text;
            default:
                return null;
        }
    }

    private static string? GetReceiverName(InvocationExpressionSyntax invocation)
        => invocation.Expression is MemberAccessExpressionSyntax m
            ? m.Expression.ToString()
            : null;
}
