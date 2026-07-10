using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.Common.Pagination;
using SimPle.Application.People.Services;
using SimPle.Domain.Users;

namespace SimPle.UnitTests.People;

public sealed class PeopleServiceTests
{
    private readonly IFriendRepository _friends = Substitute.For<IFriendRepository>();
    private readonly IFileStorageService _storage = Substitute.For<IFileStorageService>();
    private readonly PeopleService _service;

    public PeopleServiceTests()
    {
        var storageOptions = Options.Create(new StorageOptions { ReadUrlExpiryMinutes = 15 });
        _storage.CreatePresignedReadUrlAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"https://cdn.test/{ci.ArgAt<string>(0)}");
        _service = new PeopleService(_friends, _storage, storageOptions);
    }

    private static User MakeUser(string username = "carol") =>
        User.Create(username, $"{username}@test.com", "hash", char.ToUpper(username[0]) + username[1..]);

    // ── Query normalization / validation ──────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    public async Task Search_QueryTooShort_ValidationFailed(string query)
    {
        var result = await _service.SearchAsync(Guid.NewGuid(), query, 20, null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Validation.Failed");
    }

    [Fact]
    public async Task Search_QueryTooLong_ValidationFailed()
    {
        var result = await _service.SearchAsync(Guid.NewGuid(), new string('a', 101), 20, null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Validation.Failed");
    }

    [Fact]
    public async Task Search_StripsLeadingAtAndUppercasesBeforeQuerying()
    {
        var actorId = Guid.NewGuid();
        _friends.SearchPeopleAsync(actorId, "CAROL", 20, null, null, null)
            .Returns(new List<(User user, int bucket, int mutualCount, string relationshipState)>());

        var result = await _service.SearchAsync(actorId, "@carol", 20, null);

        result.IsSuccess.Should().BeTrue();
        await _friends.Received(1).SearchPeopleAsync(actorId, "CAROL", 20, null, null, null);
    }

    // ── Mapping ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_MapsRowsToResultDtos()
    {
        var actorId = Guid.NewGuid();
        var carol = MakeUser("carol");
        _friends.SearchPeopleAsync(actorId, "CAROL", 20, null, null, null)
            .Returns(new List<(User user, int bucket, int mutualCount, string relationshipState)>
            {
                (carol, 0, 2, "None"),
            });

        var result = await _service.SearchAsync(actorId, "carol", 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Items.Should().ContainSingle();
        var dto = result.Value.Items[0];
        dto.UserId.Should().Be(carol.Id);
        dto.Username.Should().Be("carol");
        dto.VisibleMutualFriendCount.Should().Be(2);
        dto.RelationshipState.Should().Be("None");
    }

    // ── Cursor paging ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_FullPage_EncodesNextCursor()
    {
        var actorId = Guid.NewGuid();
        var carol = MakeUser("carol");
        _friends.SearchPeopleAsync(actorId, "CAROL", 1, null, null, null)
            .Returns(new List<(User user, int bucket, int mutualCount, string relationshipState)>
            {
                (carol, 1, 0, "None"),
            });

        var result = await _service.SearchAsync(actorId, "carol", 1, null);

        result.IsSuccess.Should().BeTrue();
        result.Value!.NextCursor.Should().NotBeNull();
    }

    [Fact]
    public async Task Search_PartialPage_NoNextCursor()
    {
        var actorId = Guid.NewGuid();
        _friends.SearchPeopleAsync(actorId, "CAROL", 20, null, null, null)
            .Returns(new List<(User user, int bucket, int mutualCount, string relationshipState)>());

        var result = await _service.SearchAsync(actorId, "carol", 20, null);

        result.IsSuccess.Should().BeTrue();
        result.Value!.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task Search_CursorQueryMismatch_InvalidCursor()
    {
        var actorId = Guid.NewGuid();
        var cursor = Cursor.EncodePeopleSearch(0, "BOB", Guid.NewGuid(), "BOB", 1);

        var result = await _service.SearchAsync(actorId, "carol", 20, cursor);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Pagination.InvalidCursor");
    }

    [Fact]
    public async Task Search_CursorRankingVersionMismatch_InvalidCursor()
    {
        var actorId = Guid.NewGuid();
        var cursor = Cursor.EncodePeopleSearch(0, "CAROL", Guid.NewGuid(), "CAROL", 99);

        var result = await _service.SearchAsync(actorId, "carol", 20, cursor);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Pagination.InvalidCursor");
    }

    [Fact]
    public async Task Search_ValidCursor_PassesDecodedPositionToRepository()
    {
        var actorId = Guid.NewGuid();
        var afterId = Guid.NewGuid();
        var cursor = Cursor.EncodePeopleSearch(1, "BOB", afterId, "CAROL", 1);
        _friends.SearchPeopleAsync(actorId, "CAROL", 20, 1, "BOB", afterId)
            .Returns(new List<(User user, int bucket, int mutualCount, string relationshipState)>());

        var result = await _service.SearchAsync(actorId, "carol", 20, cursor);

        result.IsSuccess.Should().BeTrue();
        await _friends.Received(1).SearchPeopleAsync(actorId, "CAROL", 20, 1, "BOB", afterId);
    }
}
