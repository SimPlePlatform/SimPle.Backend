using FluentAssertions;
using SimPle.Domain.Games;

namespace SimPle.UnitTests.Games;

/// <summary>Pure in-memory domain tests for <see cref="UserFavoriteGame"/> favorite/unfavorite cycling.</summary>
public sealed class UserFavoriteGameTests
{
    [Fact]
    public void Favorite_StartsActive_WithCycleIdOne()
    {
        var favorite = UserFavoriteGame.Favorite(Guid.NewGuid(), Guid.NewGuid());

        favorite.IsActive.Should().BeTrue();
        favorite.CycleId.Should().Be(1);
    }

    [Fact]
    public void Unfavorite_SetsInactive_WithoutBumpingCycleId()
    {
        var favorite = UserFavoriteGame.Favorite(Guid.NewGuid(), Guid.NewGuid());

        favorite.Unfavorite();

        favorite.IsActive.Should().BeFalse();
        favorite.CycleId.Should().Be(1);
    }

    [Fact]
    public void Refavorite_SetsActive_AndBumpsCycleId()
    {
        var favorite = UserFavoriteGame.Favorite(Guid.NewGuid(), Guid.NewGuid());
        favorite.Unfavorite();

        favorite.Refavorite();

        favorite.IsActive.Should().BeTrue();
        favorite.CycleId.Should().Be(2);
    }

    [Fact]
    public void Unfavorite_WhenAlreadyInactive_IsNoOp()
    {
        var favorite = UserFavoriteGame.Favorite(Guid.NewGuid(), Guid.NewGuid());
        favorite.Unfavorite();

        favorite.Unfavorite();

        favorite.IsActive.Should().BeFalse();
        favorite.CycleId.Should().Be(1);
    }

    [Fact]
    public void Refavorite_WhenAlreadyActive_IsNoOp()
    {
        var favorite = UserFavoriteGame.Favorite(Guid.NewGuid(), Guid.NewGuid());

        favorite.Refavorite();

        favorite.IsActive.Should().BeTrue();
        favorite.CycleId.Should().Be(1);
    }
}
