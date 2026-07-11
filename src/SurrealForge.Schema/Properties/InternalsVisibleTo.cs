// SPDX-License-Identifier: UNLICENSED
// Expose internal parsing/diff primitives to the test assemblies so they can be
// unit-tested directly (the DDL-token extractor, the widening heuristic, the
// INFO-response parsers) without going through the full reconcile round-trip.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SurrealForge.Schema.Tests")]
[assembly: InternalsVisibleTo("SurrealForge.Client.IntegrationTests")]
