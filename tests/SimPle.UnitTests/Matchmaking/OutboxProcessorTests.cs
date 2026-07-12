using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.Friends.Outbox;
using SimPle.Application.Outbox;
using SimPle.Domain.Outbox;
using SimPle.UnitTests.Lobbies;
using Xunit;

namespace SimPle.UnitTests.Matchmaking;

/// <summary>
/// The outbox dispatcher (<strong>D3</strong>) — the codebase's first: leasing, at-least-once delivery, the bounded
/// retry budget, and dead-lettering.
///
/// The concurrent-lease behavior (<c>FOR UPDATE SKIP LOCKED</c> across two dispatchers) is database behavior and is
/// proven against real PostgreSQL. What is proven here is the bookkeeping that decides whether an event is ever
/// delivered twice, lost, or retried forever.
/// </summary>
public sealed class OutboxProcessorTests
{
    private static readonly DateTime T0 = LobbyTestFactory.T0;

    private readonly IOutboxRepository _outbox = Substitute.For<IOutboxRepository>();
    private readonly FakeTimeProvider _clock = new(T0);
    private readonly OutboxOptions _options = new() { MaxAttempts = 3 };

    private readonly OutboxProcessor _sut;

    public OutboxProcessorTests()
    {
        _outbox.LeaseAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<(OutboxMessage, OutboxDelivery)>());

        _outbox.GetOldestPendingAgeAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns((TimeSpan?)null);

        _sut = new OutboxProcessor(
            _outbox, new PassThroughWorkerTransaction(), Options.Create(_options), _clock,
            NullLogger<OutboxProcessor>.Instance);
    }

    [Fact]
    public async Task ASuccessfullyHandledEventIsMarkedProcessed()
    {
        var (message, delivery) = Leased();
        var handler = new SpyHandler();

        var result = await _sut.DispatchAsync(handler);

        handler.Handled.Should().ContainSingle().Which.Should().Be(message.Id);
        delivery.Processed.Should().BeTrue();
        delivery.DeadLettered.Should().BeFalse();

        result.Processed.Should().Be(1);
        result.Failed.Should().Be(0);

        await _outbox.Received(1).SaveAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AHandlerThatThrowsIsRetried_NotDeadLettered_WhileItsBudgetRemains()
    {
        var (_, delivery) = Leased();   // AcquireLease already counted attempt 1
        var handler = new ThrowingHandler();

        var result = await _sut.DispatchAsync(handler);

        delivery.Processed.Should().BeFalse();
        delivery.DeadLettered.Should().BeFalse();
        result.Failed.Should().Be(1);
        result.DeadLettered.Should().Be(0);
    }

    [Fact]
    public async Task AHandlerThatKeepsThrowingIsDeadLetteredOnceTheBudgetIsSpent()
    {
        // Bounded on purpose: a permanently-broken handler retried forever would crowd out every other event in the
        // batch, which is the starvation the per-handler delivery row exists to prevent.
        var (_, delivery) = Leased(attemptsAlreadyMade: _options.MaxAttempts - 1);
        var handler = new ThrowingHandler();

        var result = await _sut.DispatchAsync(handler);

        delivery.AttemptCount.Should().Be(_options.MaxAttempts);
        delivery.DeadLettered.Should().BeTrue();
        result.DeadLettered.Should().Be(1);
    }

    [Fact]
    public async Task ADeadLetteredDeliveryNeverRecordsTheExceptionMessage()
    {
        // A dead-letter row is long-lived and widely read — exactly the kind of place PII quietly accumulates. The
        // handler's exception message could carry an event body or a user id; only its type and the attempt count
        // are safe to keep.
        var (_, delivery) = Leased(attemptsAlreadyMade: _options.MaxAttempts - 1);

        await _sut.DispatchAsync(new ThrowingHandler("user bob@example.com is in lobby 7"));

        delivery.LastError.Should().NotBeNull();
        delivery.LastError.Should().NotContain("bob@example.com");
        delivery.LastError.Should().Contain(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task OneEventFailingDoesNotPreventAnotherFromBeingProcessed()
    {
        var good = Message();
        var bad = Message();
        var goodDelivery = OutboxDelivery.Create(good.Id, "spy");
        var badDelivery = OutboxDelivery.Create(bad.Id, "spy");
        goodDelivery.AcquireLease(T0.AddMinutes(1));
        badDelivery.AcquireLease(T0.AddMinutes(1));

        GivenLeased((good, goodDelivery), (bad, badDelivery));

        var handler = new SelectivelyThrowingHandler(bad.Id);

        var result = await _sut.DispatchAsync(handler);

        goodDelivery.Processed.Should().BeTrue();
        badDelivery.Processed.Should().BeFalse();
        result.Processed.Should().Be(1);
        result.Failed.Should().Be(1);
    }

    [Fact]
    public async Task AHandlerWithNoEventTypesIsNeverLeasedFor()
    {
        var result = await _sut.DispatchAsync(new NoTypesHandler());

        result.Should().Be(OutboxDispatchResult.Idle);
        await _outbox.DidNotReceive().LeaseAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(),
            Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private (OutboxMessage Message, OutboxDelivery Delivery) Leased(int attemptsAlreadyMade = 0)
    {
        var message = Message();
        var delivery = OutboxDelivery.Create(message.Id, "spy");

        // Every prior attempt left its mark. AcquireLease is what increments the count, so replaying it is exactly
        // how a delivery that has already failed N times arrives at this pass.
        for (var i = 0; i < attemptsAlreadyMade; i++) delivery.AcquireLease(T0);

        delivery.AcquireLease(T0.AddMinutes(1));   // this pass's lease

        GivenLeased((message, delivery));
        return (message, delivery);
    }

    private void GivenLeased(params (OutboxMessage, OutboxDelivery)[] rows) =>
        _outbox.LeaseAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(rows);

    private static OutboxMessage Message() => OutboxMessage.Create(
        "Block", Guid.NewGuid(), FriendOutbox.UserBlocked, 1, 1, 1, "{}");

    private class SpyHandler : IOutboxHandler
    {
        public List<Guid> Handled { get; } = new();
        public string HandlerName => "spy";
        public IReadOnlyList<string> EventTypes { get; } = new[] { FriendOutbox.UserBlocked };

        public Task HandleAsync(OutboxMessage message, CancellationToken ct = default)
        {
            Handled.Add(message.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : IOutboxHandler
    {
        private readonly string _message;
        public ThrowingHandler(string message = "boom") => _message = message;

        public string HandlerName => "spy";
        public IReadOnlyList<string> EventTypes { get; } = new[] { FriendOutbox.UserBlocked };

        public Task HandleAsync(OutboxMessage message, CancellationToken ct = default) =>
            throw new InvalidOperationException(_message);
    }

    private sealed class SelectivelyThrowingHandler : IOutboxHandler
    {
        private readonly Guid _failOn;
        public SelectivelyThrowingHandler(Guid failOn) => _failOn = failOn;

        public string HandlerName => "spy";
        public IReadOnlyList<string> EventTypes { get; } = new[] { FriendOutbox.UserBlocked };

        public Task HandleAsync(OutboxMessage message, CancellationToken ct = default) =>
            message.Id == _failOn
                ? throw new InvalidOperationException("boom")
                : Task.CompletedTask;
    }

    private sealed class NoTypesHandler : IOutboxHandler
    {
        public string HandlerName => "none";
        public IReadOnlyList<string> EventTypes { get; } = Array.Empty<string>();
        public Task HandleAsync(OutboxMessage message, CancellationToken ct = default) => Task.CompletedTask;
    }
}
