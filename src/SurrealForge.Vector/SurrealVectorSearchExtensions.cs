// SPDX-License-Identifier: MIT
// SurrealForge.Vector -- vector search over ISurrealExecutor.
// SQL construction here is sanctioned: this namespace is on the SRDB0001
// analyzer allowlist. Design + shapes: AGENTS.md §Query surface.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SurrealForge.Client.Json;
using SurrealForge.Client.Query;
using SurrealForge.Client.Schema;

namespace SurrealForge.Vector;

/// <summary>KNN / brute-force vector search extension on <see cref="ISurrealExecutor"/>.</summary>
public static class SurrealVectorSearchExtensions
{
    /// <summary>Fallback HNSW beam width when the caller leaves <c>Ef</c> unset. See AGENTS.md §Query surface.</summary>
    internal const int DefaultEf = 40;

    /// <summary>
    /// Runs a vector search over <typeparamref name="T"/>'s table (resolved
    /// via <see cref="SurrealSchemaRegistry"/>) and returns records with their
    /// <c>dist</c> projection. The embedding is ALWAYS bound as <c>$q</c>;
    /// K/EF are validated ints formatted into the operator (SurrealDB does not
    /// parameterize them). See AGENTS.md §Query surface.
    /// </summary>
    public static async Task<IReadOnlyList<VectorSearchResult<T>>> VectorSearchAsync<T>(
        this ISurrealExecutor executor,
        string field,
        float[] query,
        VectorSearchOptions? options = null,
        CancellationToken ct = default)
    {
        if (executor is null) throw new ArgumentNullException(nameof(executor));
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (query.Length == 0)
            throw new ArgumentException("Query embedding must not be empty.", nameof(query));

        var surrealQuery = BuildSearchQuery<T>(field, query, options);
        var rows = await executor.QueryAsync<JsonElement>(surrealQuery, ct).ConfigureAwait(false);

        var results = new List<VectorSearchResult<T>>(rows.Count);
        foreach (var row in rows)
        {
            var record = row.Deserialize<T>(SurrealJsonOptions.Default);
            if (record is null) continue;

            double dist = double.NaN;
            if (row.ValueKind == JsonValueKind.Object &&
                row.TryGetProperty("dist", out var d) &&
                d.ValueKind == JsonValueKind.Number)
            {
                dist = d.GetDouble();
            }
            results.Add(new VectorSearchResult<T>(record, dist));
        }
        return results;
    }

    /// <summary>Convenience overload: k + metric + strategy without an options bag.</summary>
    public static Task<IReadOnlyList<VectorSearchResult<T>>> VectorSearchAsync<T>(
        this ISurrealExecutor executor,
        string field,
        float[] query,
        int k,
        VectorMetric metric = VectorMetric.Cosine,
        VectorSearchStrategy strategy = VectorSearchStrategy.IndexedKnn,
        CancellationToken ct = default)
        => VectorSearchAsync<T>(
            executor, field, query,
            new VectorSearchOptions { K = k, Metric = metric, Strategy = strategy },
            ct);

    /// <summary>Builds the parameterized search query (internal seam for SQL-shape tests).</summary>
    internal static SurrealQuery BuildSearchQuery<T>(
        string field, float[] query, VectorSearchOptions? options)
    {
        var o = options ?? new VectorSearchOptions();
        if (o.K <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "K must be positive.");
        if (o.Ef.HasValue && o.Ef.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "EF must be positive when supplied.");

        var table = SurrealIdentifier.ForTable(SurrealSchemaRegistry.For(typeof(T)));
        var safeField = VectorFieldPath.Validate(field, nameof(field));

        var filter = o.Filter;
        if (filter is not null)
        {
            if (filter.IsMultiStatement)
                throw new ArgumentException(
                    "VectorSearchOptions.Filter must be a single predicate fragment, not a Combine() body.",
                    nameof(options));
            if (filter.Params.ContainsKey("q"))
                throw new ArgumentException(
                    "VectorSearchOptions.Filter must not bind $q — that name is reserved for the query embedding.",
                    nameof(options));
        }

        var k = o.K.ToString(CultureInfo.InvariantCulture);
        string sql;
        switch (o.Strategy)
        {
            case VectorSearchStrategy.IndexedKnn:
            {
                // SurrealDB 3.x removed the single-argument `<|K|>` form (it was
                // the KTree/M-Tree operator) — the HNSW path now requires an
                // explicit beam width. See AGENTS.md §Query surface.
                var ef = o.Ef ?? Math.Max(o.K, DefaultEf);
                var op = "<|" + k + "," + ef.ToString(CultureInfo.InvariantCulture) + "|>";
                sql = "SELECT *, vector::distance::knn() AS dist FROM " + table +
                      " WHERE " + safeField + " " + op + " $q";
                if (filter is not null) sql += " AND (" + filter.Sql + ")";
                sql += " ORDER BY dist";
                break;
            }
            case VectorSearchStrategy.BruteForce:
            {
                string fn;
                string dir;
                switch (o.Metric)
                {
                    case VectorMetric.Cosine:
                        fn = "vector::similarity::cosine(" + safeField + ", $q)";
                        dir = "DESC"; // similarity: higher = closer
                        break;
                    case VectorMetric.Euclidean:
                        fn = "vector::distance::euclidean(" + safeField + ", $q)";
                        dir = "ASC";
                        break;
                    case VectorMetric.Manhattan:
                        fn = "vector::distance::manhattan(" + safeField + ", $q)";
                        dir = "ASC";
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(options), "Unknown VectorMetric value.");
                }
                sql = "SELECT *, " + fn + " AS dist FROM " + table;
                if (filter is not null) sql += " WHERE (" + filter.Sql + ")";
                sql += " ORDER BY dist " + dir + " LIMIT " + k;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(options), "Unknown VectorSearchStrategy value.");
        }

        var q = SurrealQuery.Of(sql).WithParam("q", query);
        if (filter is not null) q = q.WithParams(filter.Params);
        return q;
    }
}
