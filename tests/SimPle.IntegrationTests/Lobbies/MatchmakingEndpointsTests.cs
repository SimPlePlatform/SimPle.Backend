using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimPle.Domain.Capabilities;
using SimPle.Domain.Games;
using SimPle.Infrastructure.Persistence;
using SimPle.IntegrationTests.Auth;

namespace SimPle.IntegrationTests.Lobbies;

/// <summary>
/// HTTP-contract tests for the Module 6 Quick Match ticket surface (slice 6C, InMemory-backed): routing, auth, CSRF,
/// validation, the privacy-safe not-found (BOLA), the cross-table one-active-lobby-or-ticket invariant, and the
/// cancel-versus-claim race's honest 200.
///
/// <para>
/// Everything genuinely concurrent — two workers, <c>FOR UPDATE SKIP LOCKED</c>, the partial unique index that makes
/// double assignment impossible — lives in <c>MatchmakingPostgresConcurrencyTests</c>. The EF InMemory provider does
/// not enforce filtered unique indexes or row versions at all, so a claim race asserted here would pass no matter
/// what the code did, which is worse than no test.
/// </para>
/// </summary>
public sealed class MatchmakingEndpointsTests : IDisposable
{
    private const string TestPassword = "TestPassword123!";

    private readonly TestWebApplicationFactory _factory = new();

    // ── Auth & CSRF ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTicket_Anonymous_Returns401()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/matchmaking/tickets", TicketRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateTicket_MissingCsrfHeader_Returns400()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();
        client.DefaultRequestHeaders.Remove("X-Requested-With");

        var response = await client.PostAsJsonAsync("/api/matchmaking/tickets", TicketRequest());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Auth.CsrfHeaderRequired");
    }

    [Fact]
    public async Task CancelTicket_MissingCsrfHeader_Returns400()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        client.DefaultRequestHeaders.Remove("X-Requested-With");

        var response = await client.DeleteAsync($"/api/matchmaking/tickets/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Enqueue ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTicket_Returns201_WithAQueuedTicketAndAnHonestDependencyReadiness()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();

        var response = await client.PostAsJsonAsync("/api/matchmaking/tickets", TicketRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var ticket = await ReadTicketAsync(response);
        ticket.GetProperty("state").GetString().Should().Be("Queued");
        ticket.GetProperty("currentBand").GetInt32().Should().Be(100);
        ticket.GetProperty("rating").GetInt32().Should().Be(1200);
        ticket.GetProperty("ratingSourceVersion").GetString().Should().Be("provisional-1200-v1");

        // Enqueue works before Module 8 — the truth is told through dependencyReadiness, not by refusing the
        // request. `assignment` is null because a ticket that has not matched has no handoff to point at.
        ticket.GetProperty("dependencyReadiness").GetProperty("matchRuntime").GetBoolean().Should().BeFalse();
        ticket.GetProperty("assignment").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task CreateTicket_IsIdempotent_AnIdenticalRepeatReturnsTheSameTicket()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();

        var first = await ReadTicketAsync(await client.PostAsJsonAsync("/api/matchmaking/tickets", TicketRequest()));
        var second = await ReadTicketAsync(await client.PostAsJsonAsync("/api/matchmaking/tickets", TicketRequest()));

        second.GetProperty("ticketId").GetGuid().Should().Be(first.GetProperty("ticketId").GetGuid());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.MatchmakingTickets.CountAsync()).Should().Be(1, "a retry must not mint a second ticket");
    }

    [Fact]
    public async Task CreateTicket_ADifferentTicketWhileOneIsLive_Returns409AlreadyQueued()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();

        await client.PostAsJsonAsync("/api/matchmaking/tickets", TicketRequest());
        var response = await client.PostAsJsonAsync(
            "/api/matchmaking/tickets", TicketRequest(timeControlId: "rapid-10-0"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Matchmaking.AlreadyQueued");
    }

    [Fact]
    public async Task CreateTicket_WhileAlreadyInALobby_Returns409()
    {
        // The cross-table invariant, from the queue's side: one active lobby OR one active ticket, never both.
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();

        var lobby = await client.PostAsJsonAsync("/api/lobbies", CreateLobbyRequest());
        lobby.StatusCode.Should().Be(HttpStatusCode.Created);

        var response = await client.PostAsJsonAsync("/api/matchmaking/tickets", TicketRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Lobbies.AlreadyActive");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public async Task CreateTicket_RejectsAnUnsupportedPlayerCount(int playerCount)
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();

        var response = await client.PostAsJsonAsync(
            "/api/matchmaking/tickets", TicketRequest(playerCount: playerCount));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Validation.Failed");
    }

    [Fact]
    public async Task CreateTicket_ForAnUnknownCapabilityVersion_Returns409CapabilityDisabled()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();

        var response = await client.PostAsJsonAsync(
            "/api/matchmaking/tickets", TicketRequest(capabilityVersion: 99));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Lobbies.CapabilityDisabled");
    }

    // ── Status: BOLA ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTicket_AnotherUsersTicketId_Returns404_NotA403()
    {
        // A 403 would confirm the id exists. Missing and foreign ticket ids must be indistinguishable
        // (OWASP API1:2023).
        using var owner = CreateClient();
        await SignInAsync(owner);
        await SeedCatalogAsync();

        var created = await ReadTicketAsync(await owner.PostAsJsonAsync("/api/matchmaking/tickets", TicketRequest()));
        var ticketId = created.GetProperty("ticketId").GetGuid();

        using var attacker = CreateClient();
        await SignInAsync(attacker);

        var foreign = await attacker.GetAsync($"/api/matchmaking/tickets/{ticketId}");
        var missing = await attacker.GetAsync($"/api/matchmaking/tickets/{Guid.NewGuid()}");

        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Byte-identical bodies: the response must not be an oracle either.
        (await foreign.Content.ReadAsStringAsync())
            .Should().Be(await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetTicket_ByItsOwner_Returns200()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();

        var created = await ReadTicketAsync(await client.PostAsJsonAsync("/api/matchmaking/tickets", TicketRequest()));
        var ticketId = created.GetProperty("ticketId").GetGuid();

        var response = await client.GetAsync($"/api/matchmaking/tickets/{ticketId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue("a ticket is private to its owner");

        (await ReadTicketAsync(response)).GetProperty("ticketId").GetGuid().Should().Be(ticketId);
    }

    // ── Cancel ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelTicket_Returns200_AndFreesTheUserToQueueAgain()
    {
        using var client = CreateClient();
        await SignInAsync(client);
        await SeedCatalogAsync();

        var created = await ReadTicketAsync(await client.PostAsJsonAsync("/api/matchmaking/tickets", TicketRequest()));
        var ticketId = created.GetProperty("ticketId").GetGuid();

        var cancel = await client.DeleteAsync($"/api/matchmaking/tickets/{ticketId}");

        cancel.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadTicketAsync(cancel)).GetProperty("state").GetString().Should().Be("Cancelled");

        // A cancelled ticket is terminal, so it no longer occupies the user's single active slot.
        var requeue = await client.PostAsJsonAsync("/api/matchmaking/tickets", TicketRequest());
        requeue.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CancelTicket_AnotherUsersTicket_Returns404()
    {
        using var owner = CreateClient();
        await SignInAsync(owner);
        await SeedCatalogAsync();

        var created = await ReadTicketAsync(await owner.PostAsJsonAsync("/api/matchmaking/tickets", TicketRequest()));
        var ticketId = created.GetProperty("ticketId").GetGuid();

        using var attacker = CreateClient();
        await SignInAsync(attacker);

        var response = await attacker.DeleteAsync($"/api/matchmaking/tickets/{ticketId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // And it really is untouched, not merely reported as missing.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.MatchmakingTickets.AsNoTracking().FirstAsync(t => t.Id == ticketId);
        row.State.Should().Be(SimPle.Domain.Matchmaking.MatchmakingTicketState.Queued);
    }

    [Fact]
    public async Task CancelTicket_AfterAWorkerClaim_Returns200WithTheCurrentStatus_NotAnError()
    {
        // Matchmaking.CancelTooLate is a 200, per the error catalogue. The user pressed cancel in good faith and the
        // queue got there first — reporting a failure would blame them for losing a race they cannot see.
        using var client = CreateClient();
        var userId = await SignInAsync(client);
        await SeedCatalogAsync();

        var created = await ReadTicketAsync(await client.PostAsJsonAsync("/api/matchmaking/tickets", TicketRequest()));
        var ticketId = created.GetProperty("ticketId").GetGuid();

        // Simulate the worker winning the race.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ticket = await db.MatchmakingTickets.FirstAsync(t => t.Id == ticketId);
            ticket.Claim("worker-1", DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        var response = await client.DeleteAsync($"/api/matchmaking/tickets/{ticketId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadTicketAsync(response)).GetProperty("state").GetString().Should().Be("Claimed");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        }).WithCsrfHeader();

    private static async Task<JsonElement> ReadTicketAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<Guid> SignInAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var email = $"mm-{suffix}@example.com";
        var username = $"mm{suffix}";

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
        return (await db.Users.AsNoTracking().FirstAsync(u => u.Email == email)).Id;
    }

    private static object TicketRequest(
        string gameSlug = "chess-lite",
        int capabilityVersion = 1,
        string mode = "multiplayer",
        int playerCount = 2,
        string timeControlId = "blitz-3-2",
        bool rated = false) => new
    {
        GameSlug = gameSlug,
        CapabilityVersion = capabilityVersion,
        Mode = mode,
        PlayerCount = playerCount,
        TimeControlId = timeControlId,
        Rated = rated,
        Region = (string?)null,
    };

    private static object CreateLobbyRequest() => new
    {
        GameSlug = "chess-lite",
        CapabilityVersion = 1,
        Privacy = "Private",
        MaxPlayers = 2,
        TimeControlId = "blitz-3-2",
        Rated = false,
        Region = (string?)null,
        SpectatorPolicy = "Anyone",
        TieBreakRuleId = "none",
        AiFillRequested = false,
    };

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
                minPlayers: 2, maxPlayers: 4, initialLifecycle: GameLifecycle.Available,
                featuredRank: null, sortOrder: 1, artToken: "chess-lite",
                artColorA: "#9B51E0", artColorB: "#2D9CDB", artAltText: "Chess Lite abstract game artwork",
                manifestVersion: "2026.1", category: "strategy",
                tags: new[] { "classic", "logic" },
                modes: new[] { "multiplayer", "cooperative" }));
        }

        if (!await db.GameCapabilityProfiles.AnyAsync(p => p.GameSlug == "chess-lite"))
        {
            db.GameCapabilityProfiles.Add(GameCapabilityProfile.Create(
                "chess-lite", 1, minPlayers: 2, maxPlayers: 4,
                allowedModes: new[] { "multiplayer", "cooperative" },
                timeControls: new[] { "blitz-3-2", "rapid-10-0", "untimed" },
                tieBreakRules: new[] { "none", "sudden-death" },
                spectatorPolicies: new[] { "Anyone", "FriendsOnly", "Disabled" },
                ratedEligible: false, aiFillEligible: false,
                manifestVersion: "2026.1"));
        }

        await db.SaveChangesAsync();
    }

    public void Dispose() => _factory.Dispose();
}
