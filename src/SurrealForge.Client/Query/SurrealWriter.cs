// SPDX-License-Identifier: MIT
// SurrealForge.Client.Query -- the package's default, coercion-safe write
// path. Emits CREATE/UPSERT as per-field SET assignments instead of
// `CONTENT $body`, because SurrealDB 3.x mis-coerces a parameterized
// `table:id`-shaped STRING in a CONTENT body into a record id (breaking plain
// `string` columns like a catalog key `item:123`). A `SET col = $p` form
// with string-typed values wrapped in `type::string($p)` keeps them strings;
// a literal CONTENT does not coerce, but the parameterized one does — so the
// SET path is the safe default for every store.
//
// Wrapping rule (so we DON'T break real FK/record columns):
//   * string value on a property that is NOT a record/FK column
//     ([References] / [Column(Type contains "record<")]) -> wrap type::string()
//   * everything else (records, numbers, bools, datetimes, arrays, nulls) ->
//     bound as-is. Null values are OMITTED entirely (an absent field is the
//     NONE that option<T> wants; an explicit JSON null is rejected by 3.x).

#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using SurrealForge.Client.Schema;

namespace SurrealForge.Client.Query
{
    /// <summary>
    /// Builds coercion-safe <c>CREATE</c>/<c>UPSERT</c> statements from a typed
    /// POCO using per-field <c>SET</c> assignments. This is the package default
    /// write path; stores should prefer it over hand-written
    /// <c>CONTENT $body</c>.
    /// </summary>
    public static class SurrealWriter
    {
        /// <summary>Emit <c>CREATE type::record($_t,$_id) SET … RETURN AFTER</c>.</summary>
        public static SurrealQuery Create<T>(T entity) where T : ISurrealRecord, new()
            => Build("CREATE", entity);

        /// <summary>Emit <c>UPSERT type::record($_t,$_id) SET … RETURN AFTER</c>.</summary>
        public static SurrealQuery Upsert<T>(T entity) where T : ISurrealRecord, new()
            => Build("UPSERT", entity);

        /// <summary>
        /// Begin a strongly typed conditional update addressed to one record.
        /// At least one predicate and one assignment are required before the
        /// builder can emit a query.
        /// </summary>
        public static TypedUpdateOnlyBuilder<T> UpdateOnly<T>(string id)
            where T : ISurrealRecord, new()
            => new TypedUpdateOnlyBuilder<T>(id);

        /// <summary>Begin a conditional update using a table-pinned record id.</summary>
        public static TypedUpdateOnlyBuilder<T> UpdateOnly<T>(RecordId<T> id)
            where T : ISurrealRecord, new()
            => new TypedUpdateOnlyBuilder<T>(id);

        /// <summary>
        /// Begin a strongly typed, predicate-required delete addressed to one
        /// record. The deleted row is returned for affected-count inspection.
        /// </summary>
        public static TypedDeleteOnlyBuilder<T> DeleteOnly<T>(string id)
            where T : ISurrealRecord, new()
            => new TypedDeleteOnlyBuilder<T>(id);

        /// <summary>Begin a conditional delete using a table-pinned record id.</summary>
        public static TypedDeleteOnlyBuilder<T> DeleteOnly<T>(RecordId<T> id)
            where T : ISurrealRecord, new()
            => new TypedDeleteOnlyBuilder<T>(id);

        private static readonly Dictionary<Type, FieldPlan[]> _planCache = new();
        private static readonly object _cacheLock = new();

        private static SurrealQuery Build<T>(string verb, T entity) where T : ISurrealRecord, new()
        {
            if (entity is null) throw new ArgumentNullException(nameof(entity));
            var table = RecordId<T>.SchemaNameOf<T>();
            var plan = PlanFor(typeof(T));

            var sb = new StringBuilder();
            sb.Append(verb).Append(" type::record($_t, $_id) SET ");
            var paramBag = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["_t"] = table,
            };

            string? idValue = null;
            bool first = true;
            foreach (var f in plan)
            {
                var value = f.Getter(entity!);
                if (f.IsId)
                {
                    idValue = value?.ToString();
                    continue; // the id addresses the record; not a SET column
                }
                if (value is null) continue; // omit -> NONE (3.x rejects explicit null on option<T>)

                // Enum values: bind the enum NAME (PascalCase) directly. A
                // SET-bound param is serialized by the global JSON options, so a
                // per-property [JsonConverter] on the POCO does NOT apply here
                // (unlike CONTENT $body where the whole object serializes). The
                // schema INSIDE sets use the enum names verbatim, so emit those.
                value = NormalizeBoundValue(value);

                var p = "_f_" + f.Column;
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(f.Column).Append(" = ");

                // Build the bound value expression.
                var valueExpr = BoundValueExpression(f, p);

                if (f.IsReadOnly)
                {
                    // READONLY column under a single UPSERT (which must serve both
                    // create and update): SET-ting a *changed* value on an existing
                    // row is rejected by 3.x ("field is readonly"). Guard so the
                    // incoming value is only applied when the column is still NONE
                    // (i.e. on create); on update the existing value is preserved,
                    // which satisfies READONLY even when the caller re-sends the
                    // whole object with a different value.
                    sb.Append("(IF ").Append(f.Column).Append(" != NONE THEN ")
                      .Append(f.Column).Append(" ELSE ").Append(valueExpr).Append(" END)");
                }
                else
                {
                    sb.Append(valueExpr);
                }
                paramBag[p] = value;
            }

            sb.Append(" RETURN AFTER");
            paramBag["_id"] = BareId(idValue ?? throw new InvalidOperationException(
                $"{typeof(T).Name} has no [Id] value; cannot address the record for {verb}."));

            return SurrealQuery.FromBuilder(sb.ToString(), paramBag);
        }

        private static string BareId(string id)
        {
            int colon = id.IndexOf(':');
            return colon >= 0 ? id.Substring(colon + 1) : id;
        }

        // ─── field plan (cached per type) ───────────────────────────────────

        internal sealed class FieldPlan
        {
            public MemberInfo Member = null!;
            public string Column = "";
            public bool IsId;
            public bool WrapString;   // wrap string values in type::string()
            public bool WrapDecimal;  // wrap decimal values in type::decimal()
            public bool IsReadOnly;   // [ReadOnly] -> guard so update keeps original
            public bool IsOptional;
            public Type ClrType = typeof(object);
            public Func<object, object?> Getter = _ => null;
        }

        internal static FieldPlan FieldFor<T, TValue>(Expression<Func<T, TValue>> selector)
            where T : ISurrealRecord, new()
        {
            var column = ExpressionTranslator.TranslateMemberPath(selector);
            var field = PlanFor(typeof(T)).SingleOrDefault(f => f.Column == column);
            if (field is null)
                throw new InvalidOperationException(
                    $"{typeof(T).Name}.{column} is not a writable modeled field.");
            if (field.IsId)
                throw new InvalidOperationException("A record id cannot be changed by an update assignment.");
            if (field.IsReadOnly)
                throw new InvalidOperationException(
                    $"The read-only field '{column}' cannot be changed by an update assignment.");
            return field;
        }

        internal static FieldPlan? FieldForPredicateParameter<T>(string parameterName)
            where T : ISurrealRecord, new()
        {
            foreach (var field in PlanFor(typeof(T)).OrderByDescending(f => f.Column.Length))
            {
                if (parameterName == field.Column) return field;
                if (!parameterName.StartsWith(field.Column + "_", StringComparison.Ordinal)) continue;
                var suffix = parameterName.Substring(field.Column.Length + 1);
                if (suffix.Length > 0 && suffix.All(char.IsDigit)) return field;
            }
            return null;
        }

        internal static FieldPlan FieldForPredicateMember<T>(MemberInfo member)
            where T : ISurrealRecord, new()
        {
            var field = PlanFor(typeof(T)).SingleOrDefault(f => f.Member == member);
            if (field is null)
                throw new InvalidOperationException(
                    $"{typeof(T).Name}.{member.Name} is not a persisted modeled field.");
            return field;
        }

        internal static object? NormalizeBoundValue(object? value)
            => value is Enum e ? e.ToString() : value;

        internal static object? NormalizePredicateBoundValue(FieldPlan? field, object? value)
        {
            if (field is null || value is null) return value;
            var valueType = Nullable.GetUnderlyingType(field.ClrType) ?? field.ClrType;
            if (!valueType.IsEnum) return value;

            object? NormalizeOne(object? item)
            {
                if (item is not string text) return NormalizeBoundValue(item);
                try { return Enum.Parse(valueType, text, ignoreCase: true).ToString(); }
                catch (ArgumentException) { return item; }
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                var normalized = new List<object?>();
                foreach (var item in enumerable) normalized.Add(NormalizeOne(item));
                return normalized;
            }
            return NormalizeOne(value);
        }

        internal static string BoundValueExpression(FieldPlan field, string parameterName)
        {
            if (field.WrapString)
                return "type::string($" + parameterName + ")";
            if (field.WrapDecimal)
                return "type::decimal($" + parameterName + ")";
            return "$" + parameterName;
        }

        internal static string MutationIdFor<T>(string id)
            where T : ISurrealRecord, new()
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Record id must not be empty.", nameof(id));
            var colon = id.IndexOf(':');
            if (colon < 0) return id;

            var expected = RecordId<T>.SchemaNameOf<T>();
            var supplied = id.Substring(0, colon);
            if (!string.Equals(supplied, expected, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Record id table '{supplied}' does not match expected table '{expected}'.",
                    nameof(id));
            var bare = id.Substring(colon + 1);
            if (string.IsNullOrWhiteSpace(bare))
                throw new ArgumentException("Record id must not be empty.", nameof(id));
            return bare;
        }

        private static FieldPlan[] PlanFor(Type t)
        {
            lock (_cacheLock)
            {
                if (_planCache.TryGetValue(t, out var cached)) return cached;
                var plan = BuildPlan(t);
                _planCache[t] = plan;
                return plan;
            }
        }

        private static FieldPlan[] BuildPlan(Type t)
        {
            var list = new List<FieldPlan>();
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead) continue;
                // Mirror the schema scanner's column set: exclude get-only
                // (SchemaName), [NotMapped], and require a setter.
                if (!p.CanWrite) continue;
                if (p.GetCustomAttribute<NotMappedAttribute>(inherit: false) != null) continue;

                bool isId = p.GetCustomAttribute<IdAttribute>(inherit: false) != null;
                var column = ResolveColumnName(p);

                // Decide type::string() wrapping from the COLUMN TYPE, not the
                // runtime value. We wrap only string-typed columns (a `table:id`
                // string there would be wrongly coerced to a record); we must
                // NOT wrap:
                //   * record/FK columns ([References] / Column Type "record<…>")
                //     — coercion to a record is DESIRED there;
                //   * non-string columns (int/bool/datetime/array/object) —
                //     type::string() on those would corrupt the value.
                var colType = p.GetCustomAttribute<ColumnAttribute>(inherit: false)?.Type;
                bool isRecord = p.GetCustomAttribute<ReferencesAttribute>(inherit: false) != null
                    || (colType != null && colType.IndexOf("record<", StringComparison.Ordinal) >= 0);

                var clrType = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;

                bool wrap;
                bool wrapDecimal = false;
                if (isRecord)
                {
                    wrap = false;
                }
                else if (colType != null)
                {
                    // Explicit column type: wrap iff it is a SCALAR string column
                    // (`string` / `option<string>`). A container type that merely
                    // CONTAINS "string" (e.g. `array<string>`, `set<string>`) must
                    // NOT be type::string-wrapped — that would stringify the whole
                    // JSON array. Same for `object`.
                    wrap = IsScalarStringColumn(colType);
                    // A `decimal` column needs type::decimal() because the global
                    // JSON options put decimals on the wire as strings.
                    wrapDecimal = !wrap && colType.IndexOf("decimal", StringComparison.Ordinal) >= 0;
                }
                else
                {
                    // Inferred from the CLR property type: string or enum
                    // (enums serialize to their string name) -> string column.
                    wrap = clrType == typeof(string) || clrType.IsEnum;
                    wrapDecimal = !wrap && clrType == typeof(decimal);
                }

                bool isReadOnly = p.GetCustomAttribute<ReadOnlyAttribute>(inherit: false) != null;
                bool isOptional = IsOptionalField(p, colType);

                list.Add(new FieldPlan
                {
                    Member = p,
                    Column = column,
                    IsId = isId,
                    WrapString = wrap,
                    WrapDecimal = wrapDecimal,
                    IsReadOnly = isReadOnly,
                    IsOptional = isOptional,
                    ClrType = p.PropertyType,
                    Getter = MakeGetter(p),
                });
            }
            return list.ToArray();
        }

        private static Func<object, object?> MakeGetter(PropertyInfo p) => obj => p.GetValue(obj);

        private static bool IsOptionalField(PropertyInfo property, string? columnType)
        {
            if (property.GetCustomAttribute<RequiredAttribute>(inherit: false) != null) return false;

            var reference = property.GetCustomAttribute<ReferencesAttribute>(inherit: false);
            if (reference != null) return reference.Optional;

            if (!string.IsNullOrWhiteSpace(columnType))
                return columnType!.TrimStart().StartsWith("option<", StringComparison.Ordinal);
            if (property.GetCustomAttribute<OptionalAttribute>(inherit: false) != null) return true;
            if (Nullable.GetUnderlyingType(property.PropertyType) != null) return true;
            return IsNullableReferenceType(property);
        }

        private static bool IsNullableReferenceType(PropertyInfo property)
        {
            if (property.PropertyType.IsValueType) return false;
            foreach (var attribute in property.GetCustomAttributes(inherit: false))
            {
                var type = attribute.GetType();
                if (type.FullName != "System.Runtime.CompilerServices.NullableAttribute") continue;
                if (type.GetField("NullableFlags")?.GetValue(attribute) is byte[] flags && flags.Length > 0)
                    return flags[0] == 2;
            }

            var declaring = property.DeclaringType;
            while (declaring != null)
            {
                foreach (var attribute in declaring.GetCustomAttributes(inherit: false))
                {
                    var type = attribute.GetType();
                    if (type.FullName != "System.Runtime.CompilerServices.NullableContextAttribute") continue;
                    if (type.GetField("Flag")?.GetValue(attribute) is byte flag) return flag == 2;
                }
                declaring = declaring.DeclaringType;
            }
            return false;
        }

        /// <summary>
        /// True only for a SCALAR string column: <c>string</c> or
        /// <c>option&lt;string&gt;</c>. Container types that contain the word
        /// "string" (<c>array&lt;string&gt;</c>, <c>set&lt;string&gt;</c>) and
        /// <c>object</c>/<c>record&lt;…&gt;</c> return false so they are bound
        /// as-is rather than type::string-wrapped.
        /// </summary>
        private static bool IsScalarStringColumn(string colType)
        {
            var t = colType.Trim();
            // Unwrap one option<…> layer.
            const string opt = "option<";
            if (t.StartsWith(opt, StringComparison.Ordinal) && t.EndsWith(">", StringComparison.Ordinal))
                t = t.Substring(opt.Length, t.Length - opt.Length - 1).Trim();
            return t.Equals("string", StringComparison.Ordinal);
        }

        private static string ResolveColumnName(PropertyInfo p)
        {
            var col = p.GetCustomAttribute<ColumnAttribute>(inherit: false);
            if (col != null && !string.IsNullOrWhiteSpace(col.Name)) return col.Name!;
            var json = p.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: false);
            if (json != null && !string.IsNullOrEmpty(json.Name)) return json.Name;
            return SurrealNaming.ToColumnName(p.Name);
        }
    }
}
