using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SurrealForge.Client.Idempotency;
using Xunit;

namespace SurrealForge.Client.Tests.Idempotency;

public class SurrealTransientConflictTests
{
    [Theory]
    [InlineData("Transaction conflict: Resource busy, this transaction can be retried", true)]
    [InlineData("Resource busy", true)]
    [InlineData("... can be retried", true)]
    [InlineData("TRANSACTION CONFLICT", true)] // case-insensitive
    [InlineData("some other error", false)]
    [InlineData("", false)]
    public void IsRetryableConflict_matches_transient_tokens(string message, bool expected)
    {
        SurrealTransientConflict.IsRetryableConflict(new Exception(message)).Should().Be(expected);
    }

    [Fact]
    public void IsRetryableConflict_null_is_false()
    {
        SurrealTransientConflict.IsRetryableConflict(null!).Should().BeFalse();
    }

    [Fact]
    public async Task RetryOnConflictAsync_retries_until_success()
    {
        var attempts = 0;
        var result = await SurrealTransientConflict.RetryOnConflictAsync<int>(() =>
        {
            attempts++;
            if (attempts < 3)
                throw new InvalidOperationException("Transaction conflict: Resource busy, can be retried");
            return Task.FromResult(42);
        }, CancellationToken.None, maxRetries: 5);

        result.Should().Be(42);
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task RetryOnConflictAsync_propagates_when_budget_exhausted()
    {
        var attempts = 0;
        Func<Task> act = () => SurrealTransientConflict.RetryOnConflictAsync<int>(() =>
        {
            attempts++;
            throw new InvalidOperationException("Transaction conflict: can be retried");
        }, CancellationToken.None, maxRetries: 2);

        await act.Should().ThrowAsync<InvalidOperationException>();
        // 1 initial + 2 retries = 3 attempts, then the 3rd conflict propagates.
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task RetryOnConflictAsync_does_not_retry_non_conflict()
    {
        var attempts = 0;
        Func<Task> act = () => SurrealTransientConflict.RetryOnConflictAsync<int>(() =>
        {
            attempts++;
            throw new InvalidOperationException("permission denied");
        }, CancellationToken.None, maxRetries: 5);

        await act.Should().ThrowAsync<InvalidOperationException>();
        attempts.Should().Be(1);
    }
}
