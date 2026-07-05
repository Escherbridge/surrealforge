// SPDX-License-Identifier: UNLICENSED
// SurrealForge.Client.Query -- typed graph traversal surface
// (surreal-linq-graph-query Phase 4, the differentiator). Builds SurrealDB
// arrow paths (->edge->table / <-edge<-table) from the modeled [RelateEdge]
// POCOs, so a fork-lineage walk is a typed graph query instead of a raw
// string or a client-side scalar loop.
//
//   q.Key(runId).Traverse(r => r.Out<ForkedFrom>().To<QuestRun>())
//     -> SELECT ->forked_from->quest_run AS result FROM <table>:<id>
//   q.Key(runId).Traverse(r => r.In<ForkedFrom>().From<QuestRun>())
//     -> SELECT <-forked_from<-quest_run AS result FROM <table>:<id>
//
// Unbounded recursive collection (the whole ancestor chain in one statement)
// is intentionally NOT emitted here: the pinned SurrealDB's recursive path
// syntax (`.{..}`) is unstable across the 1.5/2/3.x majors (plan D10 soft-dep
// on surrealdb-major-upgrade). This surface emits single-hop arrow paths that
// parse identically on every pinned major; multi-hop ancestor reads compose
// hops or await the version pin.

#nullable enable

using System;

namespace SurrealForge.Client.Query
{
    /// <summary>Arrow direction of a graph hop.</summary>
    internal enum GraphDirection { Out, In }

    /// <summary>
    /// The traversal root handed to a <c>Traverse(...)</c> lambda. Start a hop
    /// with <see cref="Out{TEdge}"/> (outgoing <c>-&gt;edge-&gt;</c>) or
    /// <see cref="In{TEdge}"/> (incoming <c>&lt;-edge&lt;-</c>).
    /// </summary>
    public sealed class GraphRoot<T> where T : ISurrealRecord, new()
    {
        internal GraphRoot() { }

        /// <summary>Begin an outgoing hop over the <typeparamref name="TEdge"/> RELATE edge.</summary>
        public GraphHop Out<TEdge>() where TEdge : ISurrealRecord, new()
            => new GraphHop(GraphDirection.Out, RecordId<TEdge>.SchemaNameOf<TEdge>());

        /// <summary>Begin an incoming hop over the <typeparamref name="TEdge"/> RELATE edge.</summary>
        public GraphHop In<TEdge>() where TEdge : ISurrealRecord, new()
            => new GraphHop(GraphDirection.In, RecordId<TEdge>.SchemaNameOf<TEdge>());
    }

    /// <summary>
    /// A graph hop awaiting its target table. Close it with
    /// <see cref="To{TTarget}"/> (for an outgoing hop) or
    /// <see cref="From{TSource}"/> (for an incoming hop).
    /// </summary>
    public sealed class GraphHop
    {
        private readonly GraphDirection _direction;
        private readonly string _edge;

        internal GraphHop(GraphDirection direction, string edge)
        {
            _direction = direction;
            _edge = edge;
        }

        /// <summary>Resolve an outgoing hop's target table: <c>-&gt;edge-&gt;target</c>.</summary>
        public GraphPath To<TTarget>() where TTarget : ISurrealRecord, new()
        {
            if (_direction != GraphDirection.Out)
                throw new InvalidOperationException("Use .From<T>() to close an In<>() hop, not .To<T>().");
            var target = RecordId<TTarget>.SchemaNameOf<TTarget>();
            return new GraphPath("->" + _edge + "->" + target);
        }

        /// <summary>Resolve an incoming hop's source table: <c>&lt;-edge&lt;-source</c>.</summary>
        public GraphPath From<TSource>() where TSource : ISurrealRecord, new()
        {
            if (_direction != GraphDirection.In)
                throw new InvalidOperationException("Use .To<T>() to close an Out<>() hop, not .From<T>().");
            var source = RecordId<TSource>.SchemaNameOf<TSource>();
            return new GraphPath("<-" + _edge + "<-" + source);
        }
    }

    /// <summary>
    /// A resolved arrow path (e.g. <c>-&gt;forked_from-&gt;quest_run</c>). The
    /// <see cref="SurrealQuery{T}.Traverse"/> emitter wraps it as the SELECT
    /// projection of the anchored query.
    /// </summary>
    public sealed class GraphPath
    {
        internal string Path { get; }
        internal GraphPath(string path) { Path = path; }

        public override string ToString() => Path;
    }
}
