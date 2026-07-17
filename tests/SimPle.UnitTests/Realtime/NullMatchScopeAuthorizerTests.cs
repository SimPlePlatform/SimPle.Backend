using FluentAssertions;
using SimPle.Application.Realtime.Authorization;
using SimPle.Application.Realtime.Contracts;

namespace SimPle.UnitTests.Realtime;

/// <summary>
/// Match-scope realtime does not exist yet (no Module 8) — every action against it must fail closed with
/// <c>realtime.scope_not_available</c> rather than 500ing or silently succeeding.
/// </summary>
public sealed class NullMatchScopeAuthorizerTests
{
    private readonly NullMatchScopeAuthorizer _authorizer = new();

    [Theory]
    [InlineData(RealtimeAction.Subscribe)]
    [InlineData(RealtimeAction.Send)]
    [InlineData(RealtimeAction.Delete)]
    public async Task AlwaysDeniesWithScopeNotAvailable(RealtimeAction action)
    {
        var result = await _authorizer.AuthorizeAsync(Guid.NewGuid(), Guid.NewGuid(), action);

        result.IsAllowed.Should().BeFalse();
        result.ErrorCode.Should().Be(NullMatchScopeAuthorizer.ScopeNotAvailableCode);
    }

    [Fact]
    public void ScopeKind_IsMatch()
    {
        _authorizer.ScopeKind.Should().Be(RealtimeEnvelope.MatchScope);
    }
}
