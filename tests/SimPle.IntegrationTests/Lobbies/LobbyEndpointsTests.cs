using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimPle.Domain.Capabilities;
using SimPle.Domain.Friends;
using SimPle.Domain.Games;
using SimPle.Infrastructure.Persistence;
using SimPle.IntegrationTests.Auth;

namespace SimPle.IntegrationTests.Lobbies;

/// <summary>
/// HTTP-contract tests for the Module 6 lobby command surface (InMemory-backed
/// <see cref="TestWebApplicationFactory"/>): routing, auth, CSRF, validation, the privacy-safe not-found (BOLA),
/// credential-oracle prevention, and honest deferral of Start.
///
/// <para>
/// Everything genuinely concurrent lives in <c>LobbiesPostgresConcurrencyTests</c> instead, and that split is not
/// cosmetic: the EF InMemory provider does not enforce filtered unique indexes, CHECK constraints, or row
/// versions <em>at all</em>. A last-seat-join race asserted here would pass no matter what the code did, which is
/// worse than no test.
/// </para>
/// </summary>
public sealed class LobbyEndpointsTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    // ── Auth & CSRF ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Anonymous_Returns401()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/lobbies", CreateRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_MissingCsrfHeader_Returns400()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();
        client.DefaultRequestHeaders.Remove("X-Requested-With");

        var response = await client.PostAsJsonAsync("/api/lobbies", CreateRequest());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Auth.CsrfHeaderRequired");
    }

    [Fact]
    public async Task Join_MissingCsrfHeader_Returns400()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        client.DefaultRequestHeaders.Remove("X-Requested-With");

        var response = await client.PostAsJsonAsync("/api/lobbies/join", new { code = "ABCD-EFGH-JKLM" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ReturnsTheLobbyAndItsCredentialExactlyOnce()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();

        var response = await client.PostAsJsonAsync("/api/lobbies", CreateRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await ReadJsonAsync(response);
        var lobby = body.GetProperty("lobby");
        var credential = body.GetProperty("credential");

        lobby.GetProperty("state").GetString().Should().Be("Open");
        lobby.GetProperty("revision").GetInt32().Should().Be(1);
        lobby.GetProperty("seats").GetArrayLength().Should().Be(1);
        credential.GetProperty("code").GetString().Should().NotBeNullOrWhiteSpace();

        // The subsequent read of the same lobby must NOT carry the credential — it is returned at create and
        // rotate only, and a member re-reading their lobby must not be handed the secret again.
        var lobbyId = lobby.GetProperty("lobbyId").GetGuid();
        var reread = await client.GetAsync($"/api/lobbies/{lobbyId}");
        var rereadBody = await reread.Content.ReadAsStringAsync();

        rereadBody.Should().NotContain(credential.GetProperty("code").GetString()!);
        rereadBody.Should().NotContain(credential.GetProperty("linkToken").GetString()!);
    }

    [Fact]
    public async Task Create_WithNoCapabilityProfile_Returns409CapabilityDisabled()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedGameOnlyAsync();   // catalog row, but no M6 capability profile pinned to it

        var response = await client.PostAsJsonAsync("/api/lobbies", CreateRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Lobbies.CapabilityDisabled");
    }

    [Fact]
    public async Task Create_WithAnUnknownTimeControl_Returns400()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();

        var response = await client.PostAsJsonAsync(
            "/api/lobbies", CreateRequest() with { TimeControlId = "not-a-time-control" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Validation.Failed");
    }

    [Fact]
    public async Task Create_WhileAlreadyInALobby_Returns409()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();

        await client.PostAsJsonAsync("/api/lobbies", CreateRequest());
        var second = await client.PostAsJsonAsync("/api/lobbies", CreateRequest());

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync()).Should().Contain("Lobbies.AlreadyActive");
    }

    // ── BOLA: a foreign or private lobby is indistinguishable from a missing one ──

    [Fact]
    public async Task Get_APrivateLobbyBelongingToAnotherUser_Returns404_Not403()
    {
        await SeedCatalogAsync();

        using var host = CreateClient();
        await SignInAsync(host);
        var created = await host.PostAsJsonAsync("/api/lobbies", CreateRequest());
        var lobbyId = (await ReadJsonAsync(created)).GetProperty("lobby").GetProperty("lobbyId").GetGuid();

        using var outsider = CreateClient();
        await SignInAsync(outsider);

        var foreign = await outsider.GetAsync($"/api/lobbies/{lobbyId}");
        var missing = await outsider.GetAsync($"/api/lobbies/{Guid.NewGuid()}");

        // Identical status AND identical body. A 403, or a differently-worded 404, would confirm the id exists.
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await foreign.Content.ReadAsStringAsync())
            .Should().Be(await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Kick_ByAnOutsider_Returns404_NeverRevealingTheLobby()
    {
        await SeedCatalogAsync();

        using var host = CreateClient();
        await SignInAsync(host);
        var created = await host.PostAsJsonAsync("/api/lobbies", CreateRequest());
        var lobbyId = (await ReadJsonAsync(created)).GetProperty("lobby").GetProperty("lobbyId").GetGuid();

        using var outsider = CreateClient();
        var outsiderId = await SignInAsync(outsider);

        var response = await outsider.PostAsJsonAsync(
            $"/api/lobbies/{lobbyId}/kick", new { targetUserId = outsiderId, expectedRevision = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AcceptInvite_AnotherUsersInviteId_Returns404()
    {
        await SeedCatalogAsync();

        using var host = CreateClient();
        var hostId = await SignInAsync(host);
        using var friend = CreateClient();
        var friendId = await SignInAsync(friend);
        await MakeFriendsAsync(hostId, friendId);

        var created = await host.PostAsJsonAsync("/api/lobbies", CreateRequest());
        var lobbyId = (await ReadJsonAsync(created)).GetProperty("lobby").GetProperty("lobbyId").GetGuid();

        var invited = await host.PostAsJsonAsync(
            $"/api/lobbies/{lobbyId}/invites", new { inviteeUserId = friendId });
        var inviteId = (await ReadJsonAsync(invited)).GetProperty("inviteId").GetGuid();

        // A third party holding the invite id must learn nothing from it.
        using var outsider = CreateClient();
        await SignInAsync(outsider);

        var response = await outsider.PostAsync($"/api/lobbies/invites/{inviteId}/accept", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Credential join ──────────────────────────────────────────────────────

    [Fact]
    public async Task Join_WithTheRealCode_SeatsTheCaller()
    {
        await SeedCatalogAsync();

        using var host = CreateClient();
        await SignInAsync(host);
        var created = await host.PostAsJsonAsync("/api/lobbies", CreateRequest());
        var code = (await ReadJsonAsync(created)).GetProperty("credential").GetProperty("code").GetString();

        using var joiner = CreateClient();
        await SignInAsync(joiner);

        var response = await joiner.PostAsJsonAsync("/api/lobbies/join", new { code });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("seats").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Join_WithALowercasedAndUnseparatedCode_StillWorks()
    {
        await SeedCatalogAsync();

        using var host = CreateClient();
        await SignInAsync(host);
        var created = await host.PostAsJsonAsync("/api/lobbies", CreateRequest());
        var code = (await ReadJsonAsync(created)).GetProperty("credential").GetProperty("code").GetString()!;

        using var joiner = CreateClient();
        await SignInAsync(joiner);

        // A human retyping a code off a screen will not reproduce the dashes or the casing.
        var mangled = code.Replace("-", string.Empty).ToLowerInvariant();
        var response = await joiner.PostAsJsonAsync("/api/lobbies/join", new { code = mangled });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Join_WithAWrongCode_Returns404_TheSameAsAnUnknownLobby()
    {
        using var client = CreateClient();
        await SignInAsync(client);

        var response = await client.PostAsJsonAsync("/api/lobbies/join", new { code = "ZZZZ-ZZZZ-ZZZZ" });

        // 404, not 403 and not a bespoke code — the join endpoint must not be an oracle.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Lobbies.CredentialInvalid");
    }

    [Fact]
    public async Task Join_WithARotatedCode_Returns404_IdenticalToAWrongCode()
    {
        await SeedCatalogAsync();

        using var host = CreateClient();
        await SignInAsync(host);
        var created = await host.PostAsJsonAsync("/api/lobbies", CreateRequest());
        var body = await ReadJsonAsync(created);
        var lobbyId = body.GetProperty("lobby").GetProperty("lobbyId").GetGuid();
        var oldCode = body.GetProperty("credential").GetProperty("code").GetString();

        var rotated = await host.PostAsync($"/api/lobbies/{lobbyId}/credential/rotate", null);
        rotated.StatusCode.Should().Be(HttpStatusCode.OK);
        var newCode = (await ReadJsonAsync(rotated)).GetProperty("code").GetString();
        newCode.Should().NotBe(oldCode);

        using var joiner = CreateClient();
        await SignInAsync(joiner);

        var withOld = await joiner.PostAsJsonAsync("/api/lobbies/join", new { code = oldCode });
        var withWrong = await joiner.PostAsJsonAsync("/api/lobbies/join", new { code = "ZZZZ-ZZZZ-ZZZZ" });

        // The old code is dead the instant it is rotated, and it dies *indistinguishably*.
        withOld.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await withOld.Content.ReadAsStringAsync())
            .Should().Be(await withWrong.Content.ReadAsStringAsync());

        // ...and the new one works.
        var withNew = await joiner.PostAsJsonAsync("/api/lobbies/join", new { code = newCode });
        withNew.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Join_WithBothCodeAndLinkToken_Returns400()
    {
        using var client = CreateClient();
        await SignInAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/lobbies/join", new { code = "ABCD-EFGH-JKLM", linkToken = "some-token" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Join_WithTheShareLinkToken_AlsoSeatsTheCaller()
    {
        await SeedCatalogAsync();

        using var host = CreateClient();
        await SignInAsync(host);
        var created = await host.PostAsJsonAsync("/api/lobbies", CreateRequest());
        var linkToken = (await ReadJsonAsync(created))
            .GetProperty("credential").GetProperty("linkToken").GetString();

        using var joiner = CreateClient();
        await SignInAsync(joiner);

        var response = await joiner.PostAsJsonAsync("/api/lobbies/join", new { linkToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Readiness, settings, stale revision ──────────────────────────────────

    [Fact]
    public async Task SetReadiness_WithAStaleRevision_Returns409()
    {
        await SeedCatalogAsync();

        using var host = CreateClient();
        await SignInAsync(host);
        var created = await host.PostAsJsonAsync("/api/lobbies", CreateRequest());
        var body = await ReadJsonAsync(created);
        var lobbyId = body.GetProperty("lobby").GetProperty("lobbyId").GetGuid();
        var code = body.GetProperty("credential").GetProperty("code").GetString();

        using var joiner = CreateClient();
        await SignInAsync(joiner);
        await joiner.PostAsJsonAsync("/api/lobbies/join", new { code });   // bumps the revision to 2

        var response = await joiner.PutAsJsonAsync(
            $"/api/lobbies/{lobbyId}/ready", new { isReady = true, expectedRevision = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Lobbies.StaleRevision");
    }

    [Fact]
    public async Task UpdateSettings_ByANonHostMember_Returns403()
    {
        await SeedCatalogAsync();

        using var host = CreateClient();
        await SignInAsync(host);
        var created = await host.PostAsJsonAsync("/api/lobbies", CreateRequest());
        var body = await ReadJsonAsync(created);
        var lobbyId = body.GetProperty("lobby").GetProperty("lobbyId").GetGuid();
        var code = body.GetProperty("credential").GetProperty("code").GetString();

        using var joiner = CreateClient();
        await SignInAsync(joiner);
        await joiner.PostAsJsonAsync("/api/lobbies/join", new { code });

        var response = await joiner.PatchAsJsonAsync($"/api/lobbies/{lobbyId}/settings", new
        {
            gameSlug = "chess-lite",
            capabilityVersion = 1,
            privacy = "Private",
            maxPlayers = 4,
            timeControlId = "rapid-10-0",
            rated = false,
            region = "eu-west",
            spectatorPolicy = "Anyone",
            tieBreakRuleId = "none",
            aiFillRequested = false,
            expectedRevision = 2,
        });

        // 403, not 404: this caller is a seated member and already knows the lobby exists.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Lobbies.Forbidden");
    }

    // ── Honest deferral ──────────────────────────────────────────────────────

    /// <summary>
    /// The module's central promise, asserted end-to-end over HTTP: with no Module 8 registered, Start is a 503,
    /// the lobby stays Open, and nothing hands the client a room to navigate to.
    /// </summary>
    [Fact]
    public async Task Start_WithEveryoneReadyButNoMatchRuntime_Returns503_AndTheLobbyStaysOpen()
    {
        await SeedCatalogAsync();

        using var host = CreateClient();
        await SignInAsync(host);
        var created = await host.PostAsJsonAsync("/api/lobbies", CreateRequest());
        var body = await ReadJsonAsync(created);
        var lobbyId = body.GetProperty("lobby").GetProperty("lobbyId").GetGuid();
        var code = body.GetProperty("credential").GetProperty("code").GetString();

        using var joiner = CreateClient();
        await SignInAsync(joiner);
        var joined = await joiner.PostAsJsonAsync("/api/lobbies/join", new { code });
        var revision = (await ReadJsonAsync(joined)).GetProperty("revision").GetInt32();

        var readied = await joiner.PutAsJsonAsync(
            $"/api/lobbies/{lobbyId}/ready", new { isReady = true, expectedRevision = revision });
        var readyBody = await ReadJsonAsync(readied);
        readyBody.GetProperty("seats").EnumerateArray()
            .All(s => s.GetProperty("isReady").GetBoolean()).Should().BeTrue();

        var start = await host.PostAsJsonAsync($"/api/lobbies/{lobbyId}/start", new
        {
            expectedRevision = readyBody.GetProperty("revision").GetInt32(),
            idempotencyKey = Guid.NewGuid().ToString("N"),
        });

        start.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await start.Content.ReadAsStringAsync()).Should().Contain("Lobbies.MatchRuntimeUnavailable");

        // The lobby did not move, and no room exists to navigate to.
        var after = await ReadJsonAsync(await host.GetAsync($"/api/lobbies/{lobbyId}"));
        after.GetProperty("state").GetString().Should().Be("Open");
        after.GetProperty("dependencyReadiness").GetProperty("matchRuntime").GetBoolean().Should().BeFalse();
        after.GetProperty("allowedActions").EnumerateArray()
            .Select(a => a.GetString()).Should().NotContain("start");
    }

    [Fact]
    public async Task Rematch_Returns503_BecauseModule8OwnsMatchRecords()
    {
        using var client = CreateClient();
        await SignInAsync(client);

        var response = await client.PostAsync($"/api/matches/{Guid.NewGuid()}/rematch-lobbies", null);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Lobbies.MatchRuntimeUnavailable");
    }

    // ── Discovery ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPublic_ShowsPublicLobbiesAndNeverPrivateOnes()
    {
        await SeedCatalogAsync();

        using var publicHost = CreateClient();
        await SignInAsync(publicHost);
        await publicHost.PostAsJsonAsync("/api/lobbies", CreateRequest() with { Privacy = "Public" });

        using var privateHost = CreateClient();
        await SignInAsync(privateHost);
        var privateCreated = await privateHost.PostAsJsonAsync(
            "/api/lobbies", CreateRequest() with { Privacy = "Private" });
        var privateId = (await ReadJsonAsync(privateCreated))
            .GetProperty("lobby").GetProperty("lobbyId").GetGuid();

        using var browser = CreateClient();
        await SignInAsync(browser);

        var response = await browser.GetAsync("/api/lobbies?limit=20");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(privateId.ToString());

        var items = (await ReadJsonAsync(response)).GetProperty("items");
        items.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task GetPublic_WithAForgedCursor_Returns400()
    {
        using var client = CreateClient();
        await SignInAsync(client);

        var response = await client.GetAsync("/api/lobbies?cursor=not-a-real-cursor!!!");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Pagination.InvalidCursor");
    }

    [Fact]
    public async Task GetMyActive_ReportsTheLobbyAndNeverBothLobbyAndTicket()
    {
        await SeedCatalogAsync();

        using var client = CreateClient();
        await SignInAsync(client);
        await client.PostAsJsonAsync("/api/lobbies", CreateRequest());

        var response = await client.GetAsync("/api/lobbies/me/active");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);

        body.GetProperty("lobby").ValueKind.Should().NotBe(JsonValueKind.Null);
        body.GetProperty("ticketId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── Invites (R6) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Invite_ThenAccept_SeatsTheFriend_AndTheInviteIsListedFirst()
    {
        await SeedCatalogAsync();

        using var host = CreateClient();
        var hostId = await SignInAsync(host);
        using var friend = CreateClient();
        var friendId = await SignInAsync(friend);
        await MakeFriendsAsync(hostId, friendId);

        var created = await host.PostAsJsonAsync("/api/lobbies", CreateRequest());
        var lobbyId = (await ReadJsonAsync(created)).GetProperty("lobby").GetProperty("lobbyId").GetGuid();

        var invited = await host.PostAsJsonAsync(
            $"/api/lobbies/{lobbyId}/invites", new { inviteeUserId = friendId });
        invited.StatusCode.Should().Be(HttpStatusCode.Created);
        var inviteId = (await ReadJsonAsync(invited)).GetProperty("inviteId").GetGuid();

        // The invitee sees it in their own list — the dashboard's badge count is this same bounded query.
        var listed = await ReadJsonAsync(await friend.GetAsync("/api/lobbies/me/invites"));
        listed.GetArrayLength().Should().Be(1);
        listed[0].GetProperty("inviteId").GetGuid().Should().Be(inviteId);

        var accepted = await friend.PostAsync($"/api/lobbies/invites/{inviteId}/accept", null);

        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(accepted)).GetProperty("seats").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Invite_ToANonFriend_Returns400_SoAPrivateLobbyCannotBeRevealed()
    {
        await SeedCatalogAsync();

        using var host = CreateClient();
        await SignInAsync(host);
        using var stranger = CreateClient();
        var strangerId = await SignInAsync(stranger);

        var created = await host.PostAsJsonAsync("/api/lobbies", CreateRequest());
        var lobbyId = (await ReadJsonAsync(created)).GetProperty("lobby").GetProperty("lobbyId").GetGuid();

        var response = await host.PostAsJsonAsync(
            $"/api/lobbies/{lobbyId}/invites", new { inviteeUserId = strangerId });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Lobbies.InvalidTarget");
    }

    [Fact]
    public async Task RevokedInvite_CannotBeAccepted()
    {
        await SeedCatalogAsync();

        using var host = CreateClient();
        var hostId = await SignInAsync(host);
        using var friend = CreateClient();
        var friendId = await SignInAsync(friend);
        await MakeFriendsAsync(hostId, friendId);

        var created = await host.PostAsJsonAsync("/api/lobbies", CreateRequest());
        var lobbyId = (await ReadJsonAsync(created)).GetProperty("lobby").GetProperty("lobbyId").GetGuid();

        var invited = await host.PostAsJsonAsync(
            $"/api/lobbies/{lobbyId}/invites", new { inviteeUserId = friendId });
        var inviteId = (await ReadJsonAsync(invited)).GetProperty("inviteId").GetGuid();

        var revoked = await host.DeleteAsync($"/api/lobbies/{lobbyId}/invites/{inviteId}");
        revoked.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var accepted = await friend.PostAsync($"/api/lobbies/invites/{inviteId}/accept", null);

        // Revocation is immediate. This is exactly what an invite-carries-the-join-code design would have broken.
        accepted.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── Leave & host transfer ────────────────────────────────────────────────

    [Fact]
    public async Task Leave_ByTheHost_TransfersHostingToTheRemainingMember()
    {
        await SeedCatalogAsync();

        using var host = CreateClient();
        await SignInAsync(host);
        var created = await host.PostAsJsonAsync("/api/lobbies", CreateRequest());
        var body = await ReadJsonAsync(created);
        var lobbyId = body.GetProperty("lobby").GetProperty("lobbyId").GetGuid();
        var code = body.GetProperty("credential").GetProperty("code").GetString();

        using var joiner = CreateClient();
        var joinerId = await SignInAsync(joiner);
        await joiner.PostAsJsonAsync("/api/lobbies/join", new { code });

        var left = await host.PostAsync($"/api/lobbies/{lobbyId}/leave", null);
        left.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await ReadJsonAsync(await joiner.GetAsync($"/api/lobbies/{lobbyId}"));

        after.GetProperty("hostUserId").GetGuid().Should().Be(joinerId);
        after.GetProperty("seats").GetArrayLength().Should().Be(1);
        after.GetProperty("allowedActions").EnumerateArray()
            .Select(a => a.GetString()).Should().Contain("invite");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private const string TestPassword = "ValidPassword1";

    private sealed record CreateLobbyBody(
        string GameSlug, int CapabilityVersion, string Privacy, int MaxPlayers, string TimeControlId,
        bool Rated, string? Region, string SpectatorPolicy, string TieBreakRuleId, bool AiFillRequested);

    private static CreateLobbyBody CreateRequest() => new(
        "chess-lite", 1, "Private", 4, "blitz-3-2", false, "eu-west", "Anyone", "none", false);

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        }).WithCsrfHeader();

    /// <summary>Registers + logs in a fresh account and returns its user id.</summary>
    private async Task<Guid> SignInAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var email = $"lby-{suffix}@example.com";
        var username = $"lby{suffix}";

        await client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = username,
            Email = email,
            Password = TestPassword,
            ConfirmPassword = TestPassword,
            CaptchaToken = "test-captcha-token",
        });
        await client.PostAsJsonAsync("/api/auth/login", new
        {
            EmailOrUsername = email,
            Password = TestPassword,
            CaptchaToken = "test-captcha-token",
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.SingleAsync(u => u.NormalizedUsername == username.ToUpperInvariant());
        return user.Id;
    }

    private async Task MakeFriendsAsync(Guid a, Guid b)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var friendship = Friendship.Request(a, b);
        friendship.Accept(b);   // the addressee is the one who accepts
        db.Friendships.Add(friendship);
        await db.SaveChangesAsync();
    }

    /// <summary>An available catalog row plus the M6 capability profile a lobby pins to it.</summary>
    private async Task SeedCatalogAsync()
    {
        await SeedGameOnlyAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.GameCapabilityProfiles.Add(GameCapabilityProfile.Create(
            "chess-lite", 1, minPlayers: 2, maxPlayers: 4,
            allowedModes: new[] { "multiplayer", "cooperative" },
            timeControls: new[] { "blitz-3-2", "rapid-10-0", "untimed" },
            tieBreakRules: new[] { "none", "sudden-death" },
            spectatorPolicies: new[] { "Anyone", "FriendsOnly", "Disabled" },
            ratedEligible: false, aiFillEligible: false,
            manifestVersion: "2026.1"));
        await db.SaveChangesAsync();
    }

    private async Task SeedGameOnlyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (await db.Games.AnyAsync(g => g.Slug == "chess-lite")) return;

        db.Games.Add(Game.Create(
            slug: "chess-lite", name: "Chess Lite", summary: "A streamlined chess experience.",
            rulesSummary: "Chess Lite wins by checkmate.", difficulty: GameDifficulty.Medium,
            estimatedDurationMinMinutes: 10, estimatedDurationMaxMinutes: 20,
            minPlayers: 2, maxPlayers: 4, initialLifecycle: GameLifecycle.Available,
            featuredRank: null, sortOrder: 1, artToken: "chess-lite",
            artColorA: "#9B51E0", artColorB: "#2D9CDB", artAltText: "Chess Lite abstract game artwork",
            manifestVersion: "2026.1", category: "strategy",
            tags: new[] { "classic" },
            modes: new[] { "multiplayer", "cooperative" }));
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    public void Dispose() => _factory.Dispose();
}
