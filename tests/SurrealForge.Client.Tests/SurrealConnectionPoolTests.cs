using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Transaction;

namespace SurrealForge.Client.Tests;

/// <summary>
/// Pool acquisition behaviour under contention — bounded by
/// <see cref="SurrealConnectionOptions.MaxConnections"/>.
/// </summary>
public class SurrealConnectionPoolTests
{
    private static ISurrealConnection FakeConnection()
    {
        // Moq's default lifecycle is fine — we only use the pool for slot
        // accounting, never touching the connection itself.
        return new Mock<ISurrealConnection>().Object;
    }

    [Fact]
    public async Task AcquireAsync_RespectsMaxConnections_Under10WayContention()
    {
        const int max = 3;
        using var pool = new SurrealConnectionPool(
            FakeConnection(),
            new SurrealConnectionOptions { MaxConnections = max });

        var maxObservedInUse = 0;
        var maxLock = new object();

        // 10 concurrent acquires; each holds its slot for 50ms before releasing.
        // The pool must never let in-use exceed MaxConnections.
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            using var lease = await pool.AcquireAsync();
            lock (maxLock)
            {
                if (pool.InUse > maxObservedInUse) maxObservedInUse = pool.InUse;
            }
            await Task.Delay(50);
        }).ToArray();

        await Task.WhenAll(tasks);

        maxObservedInUse.Should().BeLessOrEqualTo(max,
            $"pool must never let more than {max} leases be in-flight at once");
        pool.InUse.Should().Be(0, "every lease was disposed");
    }

    [Fact]
    public async Task Acquire_AndRelease_OnDispose_FreesSlot()
    {
        using var pool = new SurrealConnectionPool(
            FakeConnection(),
            new SurrealConnectionOptions { MaxConnections = 1 });

        var lease1 = await pool.AcquireAsync();
        pool.InUse.Should().Be(1);

        lease1.Dispose();
        pool.InUse.Should().Be(0);

        // We can now acquire again — slot was released.
        var lease2 = await pool.AcquireAsync();
        pool.InUse.Should().Be(1);
        lease2.Dispose();
    }

    [Fact]
    public async Task Acquire_CancellationToken_Honored_WhenPoolExhausted()
    {
        using var pool = new SurrealConnectionPool(
            FakeConnection(),
            new SurrealConnectionOptions { MaxConnections = 1 });

        var lease = await pool.AcquireAsync();

        // Second acquire would block forever; cancel after 50ms.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var act = async () => await pool.AcquireAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        lease.Dispose();
    }
}
