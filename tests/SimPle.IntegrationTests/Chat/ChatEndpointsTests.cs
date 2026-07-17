using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimPle.Domain.Capabilities;
using SimPle.Domain.Chat;
using SimPle.Domain.Games;
using SimPle.Infrastructure.Persistence;
using SimPle.IntegrationTests.Auth;

namespace SimPle.IntegrationTests.Chat;

/// <summary>
/// REST surface tests for Module 7 backend session B (M07-B2): docs/specs/module-07-realtime-presence-chat-spec.md
/// Test Matrix -- history cursor paging, delete/tombstone, and Swagger documentation. Sending a message has no
/// REST route (<see cref="SimPle.Api.Controllers.ChatController"/>'s class doc: hub-only), so the hub happy path
/// (SendLobbyMessage -&gt; ChatMessageCreated) lives in RealtimeHubTests.cs instead. Messages here are seeded
/// directly via <see cref="AppDbContext"/> rather than sent, matching this file's REST-surface-only remit.
/// </summary>
public sealed class ChatEndpointsTests : IDisposable
{
    private const string TestPassword = "ValidPassword1";
    private readonly TestWebApplicationFactory _factory = new();

    // ── History: default limit + ordering ───────────────────────────────────────

    [Fact]
    public async Task GetHistory_DefaultLimit_ReturnsThirtyMostRecentInAscendingOrder()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();
        var lobbyId = await CreatePublicLobbyAsync(client);
        var senderId = await GetMyUserIdAsync(client);
        var bodies = await SeedMessagesAsync(lobbyId, senderId, count: 35);

        var response = await client.GetAsync($"/api/chat/lobbies/{lobbyId}/messages");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = page.GetProperty("items").EnumerateArray().ToList();

        items.Should().HaveCount(30);
        items.Select(i => i.GetProperty("body").GetString())
            .Should().Equal(bodies.TakeLast(30));
        page.GetProperty("nextCursor").GetString().Should().NotBeNullOrEmpty();
    }

    // ── History: scrollback paging covers everything, no dupes/gaps ─────────────

    [Fact]
    public async Task GetHistory_PagingBackward_CoversEveryMessageWithNoDuplicatesOrGaps()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();
        var lobbyId = await CreatePublicLobbyAsync(client);
        var senderId = await GetMyUserIdAsync(client);
        var bodies = await SeedMessagesAsync(lobbyId, senderId, count: 35);

        var firstPage = await GetHistoryPageAsync(client, lobbyId, direction: "before", cursor: null, limit: null);
        var firstItems = firstPage.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("body").GetString()).ToList();
        var firstCursor = firstPage.GetProperty("nextCursor").GetString();
        firstCursor.Should().NotBeNullOrEmpty();

        var secondPage = await GetHistoryPageAsync(client, lobbyId, direction: "before", cursor: firstCursor, limit: null);
        var secondItems = secondPage.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("body").GetString()).ToList();
        secondPage.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);

        var combined = secondItems.Concat(firstItems).ToList();
        combined.Should().Equal(bodies, "walking every scrollback page in chronological order must reconstruct " +
            "the full seeded history exactly once each, with no duplicates and no gaps");
    }

    // ── History: reconnect repair (direction=after) ──────────────────────────────

    [Fact]
    public async Task GetHistory_DirectionAfter_RepairsFromTheStartThenFromTheCursor()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();
        var lobbyId = await CreatePublicLobbyAsync(client);
        var senderId = await GetMyUserIdAsync(client);
        var bodies = await SeedMessagesAsync(lobbyId, senderId, count: 35);

        var firstPage = await GetHistoryPageAsync(client, lobbyId, direction: "after", cursor: null, limit: null);
        var firstItems = firstPage.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("body").GetString()).ToList();
        firstItems.Should().Equal(bodies.Take(30), "no cursor + after means from the start of history, ascending");
        var firstCursor = firstPage.GetProperty("nextCursor").GetString();
        firstCursor.Should().NotBeNullOrEmpty();

        var secondPage = await GetHistoryPageAsync(client, lobbyId, direction: "after", cursor: firstCursor, limit: null);
        var secondItems = secondPage.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("body").GetString()).ToList();
        secondItems.Should().Equal(bodies.Skip(30), "a cursor + after means the page immediately newer than it");
        secondPage.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── History: limit cap + rejection ───────────────────────────────────────────

    [Fact]
    public async Task GetHistory_LimitFifty_ReturnsAtMostTheCap()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();
        var lobbyId = await CreatePublicLobbyAsync(client);
        var senderId = await GetMyUserIdAsync(client);
        await SeedMessagesAsync(lobbyId, senderId, count: 56);

        var page = await GetHistoryPageAsync(client, lobbyId, direction: "before", cursor: null, limit: 50);

        page.GetProperty("items").GetArrayLength().Should().Be(50);
    }

    [Fact]
    public async Task GetHistory_LimitAboveCap_Returns400ValidationFailed()
    {
        // Validation runs before authorization/lookup (ChatService.GetHistoryAsync), so an arbitrary lobby id is
        // sufficient to prove the cap is enforced -- no lobby needs to exist for this to reject.
        using var client = CreateClient();
        await SignInAsync(client);

        var response = await client.GetAsync($"/api/chat/lobbies/{Guid.NewGuid()}/messages?limit=51");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Validation.Failed");
    }

    // ── Delete / tombstone ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteMessage_ByAuthor_TombstonesTheMessageAndPersistsAcrossHistory()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();
        var lobbyId = await CreatePublicLobbyAsync(client);
        var senderId = await GetMyUserIdAsync(client);
        var messageId = await SeedMessageAsync(lobbyId, senderId, "delete me");

        var delete = await client.DeleteAsync($"/api/chat/messages/{messageId}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var page = await GetHistoryPageAsync(client, lobbyId, direction: "before", cursor: null, limit: null);
        var item = page.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("id").GetGuid().Should().Be(messageId, "the id is never reused by a delete");
        item.GetProperty("body").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("deleted").GetBoolean().Should().BeTrue();

        // Idempotent retry: a second delete on an already-deleted message still succeeds.
        var retriedDelete = await client.DeleteAsync($"/api/chat/messages/{messageId}");
        retriedDelete.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteMessage_ByNonAuthorMember_Returns403Forbidden()
    {
        using var author = CreateClient();
        await SignInAsync(author);
        await SeedCatalogAsync();
        var lobbyId = await CreatePublicLobbyAsync(author);
        var authorId = await GetMyUserIdAsync(author);
        var messageId = await SeedMessageAsync(lobbyId, authorId, "not yours to delete");

        using var otherMember = CreateClient();
        await SignInAsync(otherMember);
        var join = await otherMember.PostAsJsonAsync("/api/lobbies/join", new { LobbyId = lobbyId });
        join.EnsureSuccessStatusCode();

        var delete = await otherMember.DeleteAsync($"/api/chat/messages/{messageId}");

        delete.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await delete.Content.ReadAsStringAsync();
        body.Should().Contain("Chat.Forbidden");
    }

    [Fact]
    public async Task DeleteMessage_MissingCsrfHeader_Returns400()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();
        var lobbyId = await CreatePublicLobbyAsync(client);
        var senderId = await GetMyUserIdAsync(client);
        var messageId = await SeedMessageAsync(lobbyId, senderId, "csrf guard");
        client.DefaultRequestHeaders.Remove("X-Requested-With");

        var delete = await client.DeleteAsync($"/api/chat/messages/{messageId}");

        delete.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await delete.Content.ReadAsStringAsync();
        body.Should().Contain("Auth.CsrfHeaderRequired");
    }

    // ── Swagger ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Swagger_DescribesChatHistoryAndDeleteRoutes()
    {
        using var client = CreateClient();

        var document = await client.GetStringAsync("/swagger/v1/swagger.json");

        document.Should().Contain("\"/api/chat/lobbies/{lobbyId}/messages\"")
            .And.Contain("\"/api/chat/messages/{messageId}\"")
            .And.Contain("Chat_GetHistory")
            .And.Contain("Chat_DeleteMessage");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true
        });
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client;
    }

    private static async Task SignInAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var email = $"chat-{suffix}@example.com";
        var username = $"chat{suffix}";

        var register = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = username,
            Email = email,
            Password = TestPassword,
            ConfirmPassword = TestPassword,
            CaptchaToken = "test-captcha-token"
        });
        register.EnsureSuccessStatusCode();

        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            EmailOrUsername = email,
            Password = TestPassword,
            CaptchaToken = "test-captcha-token"
        });
        login.EnsureSuccessStatusCode();
    }

    private static async Task<Guid> GetMyUserIdAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/profile/me");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("userId").GetGuid();
    }

    private static async Task<Guid> CreatePublicLobbyAsync(HttpClient host)
    {
        var response = await host.PostAsJsonAsync("/api/lobbies", new
        {
            GameSlug = "chess-lite",
            CapabilityVersion = 1,
            Privacy = "Public",
            MaxPlayers = 8,
            TimeControlId = "blitz-3-2",
            Rated = false,
            Region = "eu-west",
            SpectatorPolicy = "Anyone",
            TieBreakRuleId = "none",
            AiFillRequested = false
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("lobby").GetProperty("lobbyId").GetGuid();
    }

    private async Task SeedCatalogAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await db.Games.AnyAsync(g => g.Slug == "chess-lite"))
        {
            db.Games.Add(Game.Create(
                slug: "chess-lite", name: "Chess Lite", summary: "A streamlined chess experience.",
                rulesSummary: "Chess Lite wins by checkmate.", difficulty: GameDifficulty.Medium,
                estimatedDurationMinMinutes: 10, estimatedDurationMaxMinutes: 20,
                minPlayers: 2, maxPlayers: 8, initialLifecycle: GameLifecycle.Available,
                featuredRank: null, sortOrder: 1, artToken: "chess-lite",
                artColorA: "#9B51E0", artColorB: "#2D9CDB", artAltText: "Chess Lite abstract game artwork",
                manifestVersion: "2026.1", category: "strategy",
                tags: new[] { "classic" },
                modes: new[] { "multiplayer", "cooperative" }));
            await db.SaveChangesAsync();
        }

        if (!await db.GameCapabilityProfiles.AnyAsync(p => p.GameSlug == "chess-lite" && p.CapabilityVersion == 1))
        {
            db.GameCapabilityProfiles.Add(GameCapabilityProfile.Create(
                "chess-lite", 1, minPlayers: 2, maxPlayers: 8,
                allowedModes: new[] { "multiplayer", "cooperative" },
                timeControls: new[] { "blitz-3-2", "rapid-10-0", "untimed" },
                tieBreakRules: new[] { "none", "sudden-death" },
                spectatorPolicies: new[] { "Anyone", "FriendsOnly", "Disabled" },
                ratedEligible: false, aiFillEligible: false,
                manifestVersion: "2026.1"));
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Seeds <paramref name="count"/> messages directly, one second apart starting from a fixed instant,
    /// so <c>(CreatedAt, Id)</c> ordering is deterministic across the whole batch. Returns each message's body in
    /// creation (chronological) order.</summary>
    private async Task<List<string>> SeedMessagesAsync(Guid lobbyId, Guid senderId, int count)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var baseTime = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var bodies = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var body = $"message-{i:D3}";
            bodies.Add(body);
            db.ChatMessages.Add(ChatMessage.Create(
                ChatScope.Lobby, lobbyId, senderId, body, Guid.NewGuid(), baseTime.AddSeconds(i)));
        }
        await db.SaveChangesAsync();
        return bodies;
    }

    private async Task<Guid> SeedMessageAsync(Guid lobbyId, Guid senderId, string body)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var message = ChatMessage.Create(ChatScope.Lobby, lobbyId, senderId, body, Guid.NewGuid(), DateTime.UtcNow);
        db.ChatMessages.Add(message);
        await db.SaveChangesAsync();
        return message.Id;
    }

    private static async Task<JsonElement> GetHistoryPageAsync(
        HttpClient client, Guid lobbyId, string direction, string? cursor, int? limit)
    {
        var query = $"?direction={direction}";
        if (cursor is not null) query += $"&cursor={Uri.EscapeDataString(cursor)}";
        if (limit is not null) query += $"&limit={limit}";

        var response = await client.GetAsync($"/api/chat/lobbies/{lobbyId}/messages{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public void Dispose() => _factory.Dispose();
}
