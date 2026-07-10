using System;
using FluentAssertions;
using SurrealForge.Client.Idempotency;
using Xunit;

namespace SurrealForge.Client.Tests.Idempotency;

public class IdempotencyReplayTests
{
    private sealed class Payload
    {
        public string Value { get; set; } = string.Empty;
        public bool Replayed { get; set; }
    }

    // A minimal consumer-shaped result envelope, standing in for an app's own type.
    private sealed record Result(bool IsError, string Message, Payload? Value);

    private const string ReplaySuccess = "replayed";
    private const string DeserializeFailed = "cannot replay payload";
    private const string OriginalFailed = "original failed";
    private const string InProgress = "in progress, retry later";

    private static Result Replay(IdempotencyRecord record)
        => IdempotencyReplay.ReplayFromRecord<Payload, Result>(
            record,
            onSuccess: (p, msg) => new Result(false, msg, p),
            onError:   msg      => new Result(true, msg, null),
            deserialize: IdempotencyReplay.DeserializeForReplay<Payload>,
            markReplayed: p => p.Replayed = true,
            replaySuccessMessage: ReplaySuccess,
            replayDeserializeFailedMessage: DeserializeFailed,
            originalFailedMessage: OriginalFailed,
            inProgressMessage: InProgress);

    [Fact]
    public void ContentHash_is_deterministic_lowercase_hex()
    {
        var a = IdempotencyReplay.ContentHash("op|1|2|3");
        var b = IdempotencyReplay.ContentHash("op|1|2|3");
        a.Should().Be(b);
        a.Should().MatchRegex("^[0-9a-f]{64}$");
        IdempotencyReplay.ContentHash("different").Should().NotBe(a);
    }

    [Fact]
    public void Completed_with_payload_replays_success_and_marks_replayed()
    {
        var record = new IdempotencyRecord
        {
            State = IdempotencyState.Completed,
            ResultPayload = IdempotencyReplay.SerializeForReplay(new Payload { Value = "hello" }),
        };

        var result = Replay(record);

        result.IsError.Should().BeFalse();
        result.Message.Should().Be(ReplaySuccess);
        result.Value!.Value.Should().Be("hello");
        result.Value.Replayed.Should().BeTrue();
    }

    [Fact]
    public void Completed_with_unparseable_payload_returns_deserialize_failed()
    {
        var record = new IdempotencyRecord
        {
            State = IdempotencyState.Completed,
            ResultPayload = "{ not valid json",
        };

        var result = Replay(record);

        result.IsError.Should().BeTrue();
        result.Message.Should().Be(DeserializeFailed);
    }

    [Fact]
    public void Failed_with_error_surfaces_recorded_error()
    {
        var record = new IdempotencyRecord { State = IdempotencyState.Failed, Error = "boom" };
        var result = Replay(record);
        result.IsError.Should().BeTrue();
        result.Message.Should().Be("boom");
    }

    [Fact]
    public void Failed_without_error_uses_original_failed_message()
    {
        var record = new IdempotencyRecord { State = IdempotencyState.Failed, Error = null };
        var result = Replay(record);
        result.IsError.Should().BeTrue();
        result.Message.Should().Be(OriginalFailed);
    }

    [Fact]
    public void InProgress_returns_in_progress_message()
    {
        var record = new IdempotencyRecord { State = IdempotencyState.InProgress };
        var result = Replay(record);
        result.IsError.Should().BeTrue();
        result.Message.Should().Be(InProgress);
    }

    [Fact]
    public void Completed_without_payload_returns_in_progress_message()
    {
        var record = new IdempotencyRecord { State = IdempotencyState.Completed, ResultPayload = null };
        var result = Replay(record);
        result.IsError.Should().BeTrue();
        result.Message.Should().Be(InProgress);
    }
}
