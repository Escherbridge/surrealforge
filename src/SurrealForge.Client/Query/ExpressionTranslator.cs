// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Client.Query -- Expression<Func<T, bool>> to SurrealQL
// translation for SurrealQuery<T>. Walks the expression tree once and
// produces a (sql-fragment, parameters-bag) pair that the SurrealQuery
// builder layer feeds to Where/Fetch/etc.
//
// Supported nodes (per the surrealdb-schema-source-gen spec):
//   * MemberExpression on the lambda parameter -> column name resolved via
//     [JsonPropertyName] on the C# property (or snake_case fallback).
//   * ConstantExpression / closure-captured constant -> parameter binding.
//   * BinaryExpression: ==, !=, <, <=, >, >=, &&, ||
//     - `col == null` / `col != null` -> `col = NONE` / `col != NONE`.
//     - ranges express as a compound `col >= a && col <= b`.
//   * MemberExpression: bare bool col -> `col = true`; `col.HasValue` (on a
//     Nullable<T> column) -> `col != NONE`.
//   * MethodCallExpression:
//     - string.IsNullOrEmpty(x) -> `(x = NONE OR x = "")`
//     - col.StartsWith/EndsWith/Contains(v) -> string::starts_with / ends_with
//       / contains(col, $v)
//     - array.Contains(col) / col.Contains(value) -> `col INSIDE $array`
//       / `$value INSIDE col` (collection membership; string.Contains is the
//       string:: form above, not this branch).
// Enum operand values are translated through JsonStringEnumConverter so
// `WalletStatus.Active` becomes the SurrealDB string `"active"` (lowercased)
// to match the JsonNamingPolicy convention used by the source-generator
// emitter -- byte-identical to the untyped form
// `SurrealQuery.Of("SELECT * FROM wallet").Where("status = $status",
// new { status = "active" })`.
//
// Anything outside this surface throws NotSupportedException with a static
// fallback recipe so callers know exactly which untyped escape hatch to use.

#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;

namespace SurrealForge.Client.Query
{
    /// <summary>
    /// Stateless single-call translator. Use <see cref="Translate"/> to
    /// convert an <see cref="Expression{TDelegate}"/> tree of
    /// <see cref="Func{T, TResult}"/>-shape into a SurrealQL predicate
    /// fragment + parameter dictionary.
    /// </summary>
    public static class ExpressionTranslator
    {
        /// <summary>Output of a single translation pass.</summary>
        public sealed class TranslationResult
        {
            public string Sql { get; }
            public IReadOnlyDictionary<string, object?> Parameters { get; }
            public TranslationResult(string sql, IReadOnlyDictionary<string, object?> parameters)
            {
                Sql = sql;
                Parameters = parameters;
            }
        }

        /// <summary>
        /// Translate the supplied <see cref="LambdaExpression"/> body into
        /// SurrealQL. The lambda must take a single parameter of type
        /// <typeparamref name="T"/> and return <c>bool</c>.
        /// </summary>
        public static TranslationResult Translate<T>(Expression<Func<T, bool>> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            var ctx = new Visitor(predicate.Parameters[0]);
            ctx.Visit(predicate.Body);
            return new TranslationResult(ctx.SqlBuffer.ToString(), ctx.Parameters);
        }

        /// <summary>
        /// Translate a member-access projection of the form <c>x =&gt; x.Field</c>
        /// into the SurrealDB column name (resolved via
        /// <see cref="JsonPropertyNameAttribute"/> or a snake_case fallback).
        /// Used by <see cref="SurrealQuery{T}"/>'s OrderBy / Fetch lambdas.
        /// </summary>
        public static string TranslateMemberPath<T>(Expression<Func<T, object>> selector)
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            var body = selector.Body;
            // unwrap Convert -> Cast for value-typed projections that end up
            // as `Convert(x.Foo, Object)` in the expression tree.
            while (body is UnaryExpression u && u.NodeType == ExpressionType.Convert)
            {
                body = u.Operand;
            }
            if (body is MemberExpression me)
            {
                return Visitor.ResolveColumnName(me);
            }
            throw NotSupported("OrderBy / Fetch", body);
        }

        /// <summary>
        /// Translate a projection lambda of the form <c>x =&gt; new { x.F1, x.F2 }</c>
        /// into the SurrealQL SELECT field list.
        /// </summary>
        public static string TranslateProjection<T, TResult>(Expression<Func<T, TResult>> projection)
        {
            if (projection == null) throw new ArgumentNullException(nameof(projection));
            var body = projection.Body;
            if (body is NewExpression ne)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < ne.Arguments.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    var arg = ne.Arguments[i];
                    while (arg is UnaryExpression u && u.NodeType == ExpressionType.Convert)
                    {
                        arg = u.Operand;
                    }
                    if (arg is MemberExpression me)
                    {
                        sb.Append(Visitor.ResolveColumnName(me));
                    }
                    else
                    {
                        throw NotSupported("projection", arg);
                    }
                }
                return sb.ToString();
            }
            if (body is MemberExpression single)
            {
                return Visitor.ResolveColumnName(single);
            }
            throw NotSupported("projection", body);
        }

        private static NotSupportedException NotSupported(string context, Expression node)
        {
            return new NotSupportedException(
                "This predicate isn't supported by the SurrealQuery<T> typed builder (" + context + "). " +
                "Fall back to SurrealQuery.Of(...).Where(...) for this query. " +
                "Specific node: " + node.NodeType + " (" + node.GetType().Name + ").");
        }

        // ─── Visitor ─────────────────────────────────────────────────────────

        private sealed class Visitor
        {
            private readonly ParameterExpression _root;
            private readonly Dictionary<string, int> _paramNameCounts = new(StringComparer.Ordinal);
            public StringBuilder SqlBuffer { get; } = new StringBuilder();
            public Dictionary<string, object?> Parameters { get; } = new(StringComparer.Ordinal);

            public Visitor(ParameterExpression root)
            {
                _root = root;
            }

            public void Visit(Expression node)
            {
                switch (node.NodeType)
                {
                    case ExpressionType.AndAlso:
                    case ExpressionType.OrElse:
                        VisitLogical((BinaryExpression)node);
                        return;
                    case ExpressionType.Equal:
                    case ExpressionType.NotEqual:
                    case ExpressionType.LessThan:
                    case ExpressionType.LessThanOrEqual:
                    case ExpressionType.GreaterThan:
                    case ExpressionType.GreaterThanOrEqual:
                        VisitComparison((BinaryExpression)node);
                        return;
                    case ExpressionType.Call:
                        VisitMethodCall((MethodCallExpression)node);
                        return;
                    case ExpressionType.Not:
                        SqlBuffer.Append("!(");
                        Visit(((UnaryExpression)node).Operand);
                        SqlBuffer.Append(")");
                        return;
                    case ExpressionType.Convert:
                    case ExpressionType.ConvertChecked:
                        Visit(((UnaryExpression)node).Operand);
                        return;
                    case ExpressionType.MemberAccess:
                        VisitBoolMember((MemberExpression)node);
                        return;
                    case ExpressionType.Constant:
                        // Bare bool constant (true/false). Emit literally.
                        var c = (ConstantExpression)node;
                        if (c.Value is bool b)
                        {
                            SqlBuffer.Append(b ? "true" : "false");
                            return;
                        }
                        break;
                }
                throw NotSupported("predicate body", node);
            }

            private void VisitLogical(BinaryExpression node)
            {
                SqlBuffer.Append("(");
                Visit(node.Left);
                SqlBuffer.Append(node.NodeType == ExpressionType.AndAlso ? " AND " : " OR ");
                Visit(node.Right);
                SqlBuffer.Append(")");
            }

            /// <summary>
            /// A member access used as a standalone bool sub-expression. Two
            /// shapes: <c>Nullable&lt;T&gt;.HasValue</c> on a column maps to
            /// <c>&lt;col&gt; != NONE</c>; a bare bool column maps to
            /// <c>&lt;col&gt; = true</c>.
            /// </summary>
            private void VisitBoolMember(MemberExpression node)
            {
                // `col.HasValue` -> `col != NONE` (the inner expression is the
                // column; `.HasValue` is the Nullable<T> property).
                if (node.Member.Name == "HasValue"
                    && node.Expression is MemberExpression inner
                    && inner.Expression == _root)
                {
                    SqlBuffer.Append(ResolveColumnName(inner));
                    SqlBuffer.Append(" != NONE");
                    return;
                }
                // Bare bool column: `w => w.IsActive` -> `is_active = true`.
                SqlBuffer.Append(ResolveColumnName(node));
                SqlBuffer.Append(" = true");
            }

            private bool IsNullLiteral(Expression node)
            {
                while (node is UnaryExpression u
                       && (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
                {
                    node = u.Operand;
                }
                return node is ConstantExpression { Value: null };
            }

            private void VisitComparison(BinaryExpression node)
            {
                // Identify which side is the column member and which is the literal/closure value.
                var leftColumn = TryGetColumnExpression(node.Left);
                var rightColumn = TryGetColumnExpression(node.Right);

                MemberExpression columnExpr;
                Expression valueExpr;
                bool flipped = false;
                if (leftColumn != null)
                {
                    columnExpr = leftColumn;
                    valueExpr = node.Right;
                }
                else if (rightColumn != null)
                {
                    columnExpr = rightColumn;
                    valueExpr = node.Left;
                    flipped = true;
                }
                else
                {
                    throw NotSupported("comparison without column reference", node);
                }

                var column = ResolveColumnName(columnExpr);

                // Null comparison: `col == null` / `col != null` map to the
                // SurrealDB NONE sentinel, NOT a bound `$param` (a parameter
                // bound to null would compare against the JSON null literal,
                // which is distinct from an absent/NONE field in SurrealDB).
                // Only `==`/`!=` are meaningful against null; relational
                // operators against null fall through to the param path and
                // bind null verbatim (SurrealDB treats it as a no-match).
                if (IsNullLiteral(valueExpr)
                    && (node.NodeType == ExpressionType.Equal || node.NodeType == ExpressionType.NotEqual))
                {
                    SqlBuffer.Append(column);
                    SqlBuffer.Append(node.NodeType == ExpressionType.Equal ? " = NONE" : " != NONE");
                    return;
                }

                var op = OperatorFor(node.NodeType, flipped);
                // Pass the column expression's declared C# type as the
                // expected operand type so an enum literal folded to its
                // underlying int by the compiler can be re-boxed as the
                // original enum identifier. Without this, comparisons of the
                // form `w.Status == WalletStatus.Active` lose the enum -> "active"
                // mapping because the compiler may emit Constant(0, Int32).
                var expectedType = (columnExpr.Member as PropertyInfo)?.PropertyType
                                   ?? (columnExpr.Member as FieldInfo)?.FieldType;
                var value = EvaluateLiteralAs(valueExpr, expectedType);
                var paramName = AllocateParamName(column);

                SqlBuffer.Append(column);
                SqlBuffer.Append(' ');
                SqlBuffer.Append(op);
                SqlBuffer.Append(" $");
                SqlBuffer.Append(paramName);
                Parameters[paramName] = NormaliseValue(value);
            }

            private void VisitMethodCall(MethodCallExpression node)
            {
                // string.IsNullOrEmpty(member)
                if (node.Method.DeclaringType == typeof(string)
                    && node.Method.Name == nameof(string.IsNullOrEmpty)
                    && node.Arguments.Count == 1)
                {
                    var arg = node.Arguments[0];
                    var columnExpr = TryGetColumnExpression(arg);
                    if (columnExpr != null)
                    {
                        var column = ResolveColumnName(columnExpr);
                        SqlBuffer.Append("(" + column + " = NONE OR " + column + " = \"\")");
                        return;
                    }
                }
                // string.StartsWith / EndsWith / Contains on a column ->
                // SurrealDB string:: functions. Instance form only
                // (`col.StartsWith(value)`); the argument is the search literal.
                if (node.Method.DeclaringType == typeof(string)
                    && node.Object != null
                    && node.Arguments.Count >= 1
                    && (node.Method.Name == nameof(string.StartsWith)
                        || node.Method.Name == nameof(string.EndsWith)
                        || node.Method.Name == nameof(string.Contains)))
                {
                    var columnExpr = TryGetColumnExpression(node.Object);
                    if (columnExpr != null)
                    {
                        var fn = node.Method.Name switch
                        {
                            nameof(string.StartsWith) => "string::starts_with",
                            nameof(string.EndsWith)   => "string::ends_with",
                            _                          => "string::contains",
                        };
                        var column = ResolveColumnName(columnExpr);
                        var paramName = AllocateParamName(column);
                        SqlBuffer.Append(fn);
                        SqlBuffer.Append('(');
                        SqlBuffer.Append(column);
                        SqlBuffer.Append(", $");
                        SqlBuffer.Append(paramName);
                        SqlBuffer.Append(')');
                        Parameters[paramName] = NormaliseValue(EvaluateLiteral(node.Arguments[0]));
                        return;
                    }
                }

                // (IEnumerable<T>).Contains(member) or member.Contains(value).
                // string.Contains is handled above; this branch is collection
                // membership only (INSIDE), so skip it for string receivers.
                if (node.Method.Name == nameof(Enumerable.Contains)
                    && node.Object?.Type != typeof(string))
                {
                    Expression? collectionExpr = null;
                    Expression? valueExpr = null;
                    if (node.Arguments.Count == 2)
                    {
                        // static Enumerable.Contains(IEnumerable<T>, T)
                        collectionExpr = node.Arguments[0];
                        valueExpr = node.Arguments[1];
                    }
                    else if (node.Arguments.Count == 1 && node.Object != null)
                    {
                        // instance List<T>.Contains(T)
                        collectionExpr = node.Object;
                        valueExpr = node.Arguments[0];
                    }

                    if (collectionExpr != null && valueExpr != null)
                    {
                        // Branch A: column INSIDE constant-collection  (column.Contains?)
                        var memberColumn = TryGetColumnExpression(valueExpr);
                        var collectionLiteral = TryEvaluateLiteral(collectionExpr);
                        if (memberColumn != null && collectionLiteral != null)
                        {
                            var column = ResolveColumnName(memberColumn);
                            var paramName = AllocateParamName(column);
                            SqlBuffer.Append(column);
                            SqlBuffer.Append(" INSIDE $");
                            SqlBuffer.Append(paramName);
                            Parameters[paramName] = NormaliseEnumerable(collectionLiteral);
                            return;
                        }
                        // Branch B: constant-value INSIDE column-array
                        var arrayColumn = TryGetColumnExpression(collectionExpr);
                        if (arrayColumn != null)
                        {
                            var column = ResolveColumnName(arrayColumn);
                            var paramName = AllocateParamName(column);
                            var literal = EvaluateLiteral(valueExpr);
                            SqlBuffer.Append("$");
                            SqlBuffer.Append(paramName);
                            SqlBuffer.Append(" INSIDE ");
                            SqlBuffer.Append(column);
                            Parameters[paramName] = NormaliseValue(literal);
                            return;
                        }
                    }
                }
                throw NotSupported("method call", node);
            }

            // ─── Helpers ─────────────────────────────────────────────────────

            private static string OperatorFor(ExpressionType nodeType, bool flipped)
            {
                switch (nodeType)
                {
                    case ExpressionType.Equal: return "=";
                    case ExpressionType.NotEqual: return "!=";
                    case ExpressionType.LessThan: return flipped ? ">" : "<";
                    case ExpressionType.LessThanOrEqual: return flipped ? ">=" : "<=";
                    case ExpressionType.GreaterThan: return flipped ? "<" : ">";
                    case ExpressionType.GreaterThanOrEqual: return flipped ? "<=" : ">=";
                    default:
                        throw new NotSupportedException("Unsupported comparison operator: " + nodeType);
                }
            }

            private MemberExpression? TryGetColumnExpression(Expression node)
            {
                while (node is UnaryExpression u && (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
                {
                    node = u.Operand;
                }
                if (node is MemberExpression me && me.Expression == _root)
                {
                    return me;
                }
                return null;
            }

            private object? EvaluateLiteral(Expression node)
            {
                var literal = TryEvaluateLiteral(node);
                if (literal == null)
                {
                    // null might be a legitimate value; differentiate via a marker.
                    if (node is ConstantExpression ce && ce.Value == null) return null;
                    if (node is MemberExpression me && me.Expression != _root)
                    {
                        // Closure-captured null.
                        return Expression.Lambda(node).Compile().DynamicInvoke();
                    }
                }
                return literal;
            }

            /// <summary>
            /// Evaluate a literal sub-expression and re-box its value as the
            /// supplied <paramref name="expectedType"/> when the underlying
            /// .NET type differs (e.g. the C# compiler folded an
            /// <c>WalletStatus.Active</c> enum constant to its underlying
            /// <c>Int32</c> representation). Without this re-boxing, the
            /// downstream <see cref="NormaliseValue"/> path cannot recognise
            /// the value as an enum and emit the lowercase identifier form.
            /// </summary>
            private object? EvaluateLiteralAs(Expression node, Type? expectedType)
            {
                var raw = EvaluateLiteral(node);
                if (raw == null || expectedType == null) return raw;

                // If the raw value's runtime type already matches, no re-boxing needed.
                if (expectedType.IsInstanceOfType(raw)) return raw;

                // Enum re-box: int (or other underlying integral) -> Enum.
                var unwrapped = Nullable.GetUnderlyingType(expectedType) ?? expectedType;
                if (unwrapped.IsEnum && raw is IConvertible)
                {
                    try
                    {
                        return Enum.ToObject(unwrapped, raw);
                    }
                    catch
                    {
                        return raw;
                    }
                }
                return raw;
            }

            private object? TryEvaluateLiteral(Expression node)
            {
                while (node is UnaryExpression u && (u.NodeType == ExpressionType.Convert || u.NodeType == ExpressionType.ConvertChecked))
                {
                    node = u.Operand;
                }
                if (node is ConstantExpression ce)
                {
                    // ce.Value preserves the original .NET type, so an enum
                    // constant arrives as a boxed Enum (not as its underlying
                    // int). NormaliseValue downstream lower-cases the enum
                    // identifier to the SurrealDB string form.
                    return ce.Value;
                }
                if (node is MemberExpression me && me.Expression != _root)
                {
                    // Closure-captured value: compile + invoke the sub-tree to
                    // resolve the runtime value at translation time.
                    try
                    {
                        return Expression.Lambda(node).Compile().DynamicInvoke();
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException != null)
                    {
                        throw ex.InnerException;
                    }
                }
                // Any other closed-over sub-expression (method call, `new`,
                // binary arithmetic on captured values, etc.) that does NOT
                // reference the lambda parameter is a literal: compile + invoke
                // it. Without this, e.g. `w.AvatarId == SurrealLink.ToLink(...)`
                // bound an empty value (the method call fell through to null),
                // so the predicate silently matched nothing.
                if (!ReferencesRoot(node))
                {
                    try
                    {
                        return Expression.Lambda(node).Compile().DynamicInvoke();
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException != null)
                    {
                        throw ex.InnerException;
                    }
                }
                return null;
            }

            /// <summary>
            /// True if <paramref name="node"/> (or any descendant) references the
            /// lambda's row parameter. Used to decide whether a sub-expression is
            /// a closed-over literal that can be compiled + invoked at translation
            /// time (false) or a column reference that must stay symbolic (true).
            /// </summary>
            private bool ReferencesRoot(Expression node) => new RootFinder(_root).Found(node);

            private sealed class RootFinder : ExpressionVisitor
            {
                private readonly ParameterExpression _root;
                private bool _found;
                public RootFinder(ParameterExpression root) => _root = root;
                public bool Found(Expression node) { _found = false; Visit(node); return _found; }
                protected override Expression VisitParameter(ParameterExpression node)
                {
                    if (node == _root) _found = true;
                    return base.VisitParameter(node);
                }
            }

            private string AllocateParamName(string columnName)
            {
                // Parameter name = column name; subsequent uses of same column
                // suffix with _2, _3, ... so the parameter dictionary stays
                // unambiguous when a predicate references the same column twice.
                if (!_paramNameCounts.TryGetValue(columnName, out var count) || count == 0)
                {
                    _paramNameCounts[columnName] = 1;
                    return columnName;
                }
                count++;
                _paramNameCounts[columnName] = count;
                return columnName + "_" + count.ToString(CultureInfo.InvariantCulture);
            }

            // Translate enum constants to their JsonStringEnumConverter shape
            // (lowercase by default, matching the source-generator emitter's
            // JsonNamingPolicy convention) so the typed builder produces
            // byte-identical SurrealQL to the untyped equivalent.
            private static object? NormaliseValue(object? value)
            {
                if (value == null) return null;
                if (value is Enum e)
                {
                    return e.ToString().ToLowerInvariant();
                }
                return value;
            }

            private static object? NormaliseEnumerable(object? value)
            {
                if (value is IEnumerable enumerable && value is not string)
                {
                    var list = new List<object?>();
                    foreach (var item in enumerable)
                    {
                        list.Add(NormaliseValue(item));
                    }
                    return list;
                }
                return NormaliseValue(value);
            }

            // ─── Column resolution (visible to OrderBy / projection paths) ───

            internal static string ResolveColumnName(MemberExpression me)
            {
                var member = me.Member;
                // Prefer JsonPropertyNameAttribute when present.
                var attr = member.GetCustomAttribute<JsonPropertyNameAttribute>();
                if (attr != null && !string.IsNullOrEmpty(attr.Name))
                {
                    return attr.Name;
                }
                // Fallback: snake_case the C# member name.
                return ToSnakeCase(member.Name);
            }

            private static string ToSnakeCase(string pascal)
            {
                if (string.IsNullOrEmpty(pascal)) return pascal;
                var sb = new StringBuilder(pascal.Length + 4);
                for (int i = 0; i < pascal.Length; i++)
                {
                    char c = pascal[i];
                    if (char.IsUpper(c))
                    {
                        if (i > 0) sb.Append('_');
                        sb.Append(char.ToLowerInvariant(c));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                return sb.ToString();
            }
        }
    }
}
