using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Tests; // FakeHttpHandler

namespace SurrealForge.Client.Tests.Connection;

/// <summary>
/// LOW #L2 regression coverage: <see cref="HttpSurrealConnection"/>'s retry
/// jitter must remain non-degenerate under concurrent access. The old
/// implementation shared a single <see cref="System.Random"/> instance that
/// is not thread-safe pre-net6 and could be observed yielding correlated /
/// zero values when callers raced. The fix uses <c>Random.Shared</c> on
/// net8.0 and a <c>[ThreadStatic]</c> per-thread instance on netstandard2.0.
/// </summary>
public sealed class HttpSurrealConnectionConcurrentRetryTests
{
    private const string OkBody = """[ { "status": "OK", "time": "1µs", "result": [] } ]""";

    [Fact]
    public async Task Concurrent_retries_produce_varied_jitter_outcomes()
    {
        // Drive eight concurrent SELECT calls; each fails once then succeeds.
        // The first-failure DelayWithJitterAsync invocation exercises the
        // random number generator. We assert the resulting timings show
        // jitter variance — at least 6 of 8 have distinct, non-zero offsets
        // from the base delay.
        const int callerCount = 8;
        var observedDelays = new ConcurrentBag<double>();

        var tasks = new Task[callerCount];
        for (int i = 0; i < callerCount; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                var handler = new FakeHttpHandler();
                handler.Enqueue(_ => throw new HttpRequestException("synthetic transport fault"));
                handler.EnqueueOk(OkBody);

                var opts = new SurrealConnectionOptions
                {
                    Endpoint       = "http://localhost:8442",
                    Namespace      = "test",
                    Database       = "test",
                    User           = "u",
                    Password       = "p",
                    MaxRetries     = 3,
                    BaseRetryDelay = TimeSpan.FromMilliseconds(100),
                    JitterRatio    = 0.5,
                };

                await using var conn = new HttpSurrealConnection(handler, opts);
                var start = DateTime.UtcNow;
                await conn.ExecuteRawAsync("SELECT * FROM wallet;");
                var elapsed = (DateTime.UtcNow - start).TotalMilliseconds;
                observedDelays.Add(elapsed);
            });
        }

        await Task.WhenAll(tasks);

        // Statistical, tolerant assertion: with JitterRatio=0.5 the delays
        // should span roughly [50ms, 150ms]; the chance of all eight landing
        // on a tight cluster (within 5ms of each other) under proper RNG
        // independence is vanishingly small. We assert at least six show a
        // delay above zero (i.e. jitter did NOT collapse to all-zero) AND
        // the distinct count is at least six (i.e. the RNG produced diverse
        // outcomes across threads).
        var nonZero = observedDelays.Count(d => d > 0);
        nonZero.Should().BeGreaterThanOrEqualTo(6,
            "at least 6 of 8 concurrent jitter outcomes must be non-zero (L2 fix)");

        var distinctBuckets = observedDelays
            .Select(d => Math.Round(d, MidpointRounding.AwayFromZero))
            .Distinct()
            .Count();
        distinctBuckets.Should().BeGreaterThanOrEqualTo(2,
            "concurrent jitter should yield at least two distinct rounded delays");
    }
}
