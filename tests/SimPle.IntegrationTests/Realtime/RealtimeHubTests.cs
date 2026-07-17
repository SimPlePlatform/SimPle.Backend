using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimPle.Domain.Capabilities;
using SimPle.Domain.Games;
using SimPle.Infrastructure.Persistence;
using SimPle.IntegrationTests.Auth;

namespace SimPle.IntegrationTests.Realtime;

/// <summary>
/// Hub-level (not just unit-level) verification for Module 7 backend session A (M07-B1):
/// docs/specs/module-07-realtime-presence-chat-spec.md. Connects real <see cref="HubConnection"/>s against the
/// in-memory <see cref="TestWebApplicationFactory"/> host over LongPolling (a real HTTP transport the TestServer's
/// fake handler supports end to end, unlike raw WebSockets) so that authentication, the origin allowlist
/// middleware, the connection cap, per-method scope authorization, proactive close, and the message-size cap are
/// all exercised through the actual wire path rather than by calling internal classes directly.
///
/// NOT covered here: a live "match" scope request. The B1 hub surface never exposes a method that routes to the
/// "match" scope kind (no SubscribeMatch exists — see RealtimeHub.cs) so <c>realtime.scope_not_available</c> is
/// only reachable at the unit level today (NullMatchScopeAuthorizerTests). Calling that out rather than inventing
/// a hub method to force the path, which would be a production-code change outside this file's remit.
/// </summary>
public sealed class RealtimeHubTests : IDisposable
{
    private const string TestPassword = "ValidPassword1";
    private const string HubPath = "/hubs/realtime";
    private const string AllowedOrigin = "http://localhost:3000";

    private readonly TestWebApplicationFactory _factory = new();

    // ── 1. Authenticated cookie connect + origin mismatch ───────────────────────

    [Fact]
    public async Task Connect_WithValidCookieAndAllowedOrigin_Succeeds()
    {
        using var client = CreateClient();
        var cookies = await SignInAsync(client);

        await using var connection = CreateHubConnection(cookies, AllowedOrigin);

        await connection.StartAsync();

        connection.State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public async Task Connect_WithMismatchedOrigin_IsRefused()
    {
        using var client = CreateClient();
        var cookies = await SignInAsync(client);

        await using var connection = CreateHubConnection(cookies, "http://evil.example.com");

        var act = () => connection.StartAsync();

        await act.Should().ThrowAsync<Exception>();
        connection.State.Should().Be(HubConnectionState.Disconnected);
    }

    /// <summary>M07-003 regression: a request to the hub with no Origin header at all must be rejected the same
    /// as a present-but-unlisted one — a missing header is not an implicit pass. Uses a raw <see cref="HttpClient"/>
    /// negotiate POST rather than <see cref="HubConnection"/>, since the SignalR client always sets Origin itself
    /// and offers no way to omit it.</summary>
    [Fact]
    public async Task Connect_WithMissingOriginHeader_IsRefusedWith403()
    {
        using var client = CreateClient();
        await SignInAsync(client);

        var response = await client.PostAsync(HubPath + "/negotiate?negotiateVersion=1", new StringContent(""));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── 2. Connection cap ────────────────────────────────────────────────────────

    [Fact]
    public async Task SixthConcurrentConnection_ForSameUser_IsRejectedWithConnectionLimit()
    {
        using var client = CreateClient();
        var cookies = await SignInAsync(client);

        var connections = new List<HubConnection>();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var connection = CreateHubConnection(cookies, AllowedOrigin);
                await connection.StartAsync();
                connections.Add(connection);
            }

            await using var sixth = CreateHubConnection(cookies, AllowedOrigin);
            var closedTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            sixth.Closed += ex =>
            {
                closedTcs.TrySetResult(ex);
                return Task.CompletedTask;
            };

            // A HubException thrown from OnConnectedAsync does not necessarily fault StartAsync itself: the
            // SignalR protocol handshake response can already have been sent to the client before the hub's
            // OnConnectedAsync override runs server-side (confirmed via server-side logs: "Realtime connection
            // rejected: connection limit reached" followed by "HubException: Realtime.ConnectionLimit" from
            // OnConnectedAsync, then the connection is torn down). Both a synchronous throw here and a
            // just-connected-then-immediately-closed connection are accepted proof of rejection.
            try
            {
                await sixth.StartAsync();
            }
            catch
            {
                // Rejected synchronously during the handshake (e.g. via the origin/auth path) -- also acceptable.
            }

            // KNOWN FLAKE under full-solution runs (documented, not a production defect): this project has no
            // [CollectionDefinition(DisableParallelization = true)], so xUnit runs every integration test
            // collection concurrently (default max degree of parallelism == CPU core count). A full `dotnet test
            // SimPle.sln` run competes dozens of WebApplicationFactory/Postgres-backed hosts plus 898 unit tests
            // for the same cores, which can starve the thread pool badly enough that a LongPolling client fails
            // to observe a server-initiated abort within even a generous window — this is an observation-latency
            // artifact of full-suite parallel contention, not evidence the connection limit stopped being
            // enforced (the rate limiter and presence registry are per-host singletons, unaffected by other
            // hosts' load; see RealtimeRateLimiter/PresenceRegistry). Reproduced failures at 10s, 25s, and 60s
            // under full-suite load; passes reliably in isolation and under capped parallelism
            // (`dotnet test -- xUnit.MaxParallelThreads=4`). Not fixed here because the real fix (capping
            // parallelism suite-wide) is a shared test-infrastructure change outside M07-B1's scope — see the
            // M07-B1 checkpoint report for the accepted tradeoff.
            if (sixth.State == HubConnectionState.Connected)
                await Task.WhenAny(closedTcs.Task, Task.Delay(TimeSpan.FromSeconds(60)));

            sixth.State.Should().Be(HubConnectionState.Disconnected,
                "the 6th concurrent connection for the same user must be rejected (Realtime.ConnectionLimit), " +
                "surfacing here as the connection closing shortly after connecting rather than staying live");
        }
        finally
        {
            foreach (var connection in connections)
                await connection.DisposeAsync();
        }
    }

    // ── 3. Privacy-safe not-found via SubscribeLobby ────────────────────────────

    [Fact]
    public async Task SubscribeLobby_ToANonexistentLobby_ReturnsPrivacySafeNotFound()
    {
        using var client = CreateClient();
        var cookies = await SignInAsync(client);

        await using var connection = CreateHubConnection(cookies, AllowedOrigin);
        await connection.StartAsync();

        var act = () => connection.InvokeAsync<object>("SubscribeLobby", Guid.NewGuid());

        var exception = await act.Should().ThrowAsync<Exception>();
        exception.And.Message.Should().Contain("Lobbies.NotFound");
    }

    [Fact]
    public async Task SubscribeLobby_ToAPrivateLobbyBelongingToAnotherUser_ReturnsTheSamePrivacySafeNotFound()
    {
        using var host = CreateClient();
        await SignInAsync(host);
        await SeedCatalogAsync();
        var lobbyId = await CreatePrivateLobbyAsync(host);

        using var outsider = CreateClient();
        var outsiderCookies = await SignInAsync(outsider);

        await using var connection = CreateHubConnection(outsiderCookies, AllowedOrigin);
        await connection.StartAsync();

        var act = () => connection.InvokeAsync<object>("SubscribeLobby", lobbyId);

        var exception = await act.Should().ThrowAsync<Exception>();
        exception.And.Message.Should().Contain("Lobbies.NotFound");
    }

    // ── 4. Proactive close on logout ────────────────────────────────────────────

    [Fact]
    public async Task Logout_ProactivelyClosesTheLiveHubConnection()
    {
        using var client = CreateClient();
        var cookies = await SignInAsync(client);

        await using var connection = CreateHubConnection(cookies, AllowedOrigin);
        var closedTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += ex =>
        {
            closedTcs.TrySetResult(ex);
            return Task.CompletedTask;
        };
        await connection.StartAsync();
        connection.State.Should().Be(HubConnectionState.Connected);

        var logout = await client.PostAsync("/api/auth/logout", null);
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var completed = await Task.WhenAny(closedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        completed.Should().Be(closedTcs.Task, "logout must proactively abort the live realtime connection " +
            "rather than waiting for token expiry or the next per-method recheck");
    }

    // ── 5. Oversized message rejected without disabling the buffer limit ───────

    [Fact]
    public async Task OversizedMessage_IsRejected_AndTheCapIsNeverGloballyDisabled()
    {
        using var client = CreateClient();
        var cookies = await SignInAsync(client);

        await using var connection = CreateHubConnection(cookies, AllowedOrigin);
        var closedTcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += ex =>
        {
            closedTcs.TrySetResult(ex);
            return Task.CompletedTask;
        };
        await connection.StartAsync();

        // 20 KiB of payload in a single argument is well over the 16 KiB cap (MaximumReceiveMessageSize /
        // ApplicationMaxBufferSize, both set in Program.cs) — sent against the existing ReportActivity method
        // (which normally takes no arguments) purely to force an oversized wire frame; the cap is enforced by
        // the hub protocol parser before argument binding, so the extra argument never needs to be consumed.
        var oversizedPayload = new string('a', 20 * 1024);

        // SendAsync is fire-and-forget (no completion is expected for an over-cap frame the server can't even
        // parse), so the assertion is on the connection being torn down, not on an exception from this call.
        try
        {
            await connection.SendAsync("ReportActivity", oversizedPayload);
        }
        catch
        {
            // Some transports surface the rejection synchronously as a thrown exception instead of only via
            // Closed — either outcome proves the cap is enforced, so this is deliberately swallowed here.
        }

        var completed = await Task.WhenAny(closedTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        completed.Should().Be(closedTcs.Task,
            "an over-cap message must cause the connection to be torn down, proving MaximumReceiveMessageSize " +
            "is enforced rather than disabled");

        // Proof the cap was never globally disabled elsewhere: a fresh, well-formed connection against the same
        // running host still connects and calls a real method successfully right after the oversized rejection.
        using var otherClient = CreateClient();
        var otherCookies = await SignInAsync(otherClient);
        await using var otherConnection = CreateHubConnection(otherCookies, AllowedOrigin);
        await otherConnection.StartAsync();
        await otherConnection.InvokeAsync("ReportActivity");
        otherConnection.State.Should().Be(HubConnectionState.Connected);
    }

    // ── 6. Chat hub happy path (M07-B2) ─────────────────────────────────────────

    /// <summary>docs/specs/module-07-realtime-presence-chat-spec.md Test Matrix: "Hub happy path: connect with
    /// cookie → SubscribeLobby → receive LobbyChanged → SendLobbyMessage → ChatMessageCreated." The lobby-create
    /// itself is what fires the LobbyChanged the spec asks for (see LobbyRealtimeHandler); SendLobbyMessage is
    /// invoked only after that first hint has actually been observed, so the ordering in the assertion mirrors the
    /// spec's own phrasing rather than just proving the two events arrive at some point.</summary>
    [Fact]
    public async Task SendLobbyMessage_FansOutChatMessageCreated_ToSubscribedConnections()
    {
        using var host = CreateClient();
        await SignInAsync(host);
        await SeedCatalogAsync();
        var lobbyId = await CreatePublicLobbyAsync(host);

        using var listenerClient = CreateClient();
        var listenerCookies = await SignInAsync(listenerClient);

        await using var connection = CreateHubConnection(listenerCookies, AllowedOrigin);

        var lobbyChangedTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<object, int, string>("LobbyChanged", (_, revision, _) => lobbyChangedTcs.TrySetResult(revision));

        var chatMessageTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<object, JsonElement>("ChatMessageCreated", (_, message) => chatMessageTcs.TrySetResult(message));

        await connection.StartAsync();
        await connection.InvokeAsync<object>("SubscribeLobby", lobbyId);

        // The listener joining the lobby is what fires the LobbyChanged hint this connection must observe before
        // SendLobbyMessage is invoked, proving SubscribeLobby actually took effect first (Lobbies_Join: a
        // Public+Open lobby can be joined by naming its id directly, no code/link token needed).
        var join = await listenerClient.PostAsJsonAsync("/api/lobbies/join", new { LobbyId = lobbyId });
        join.EnsureSuccessStatusCode();

        // The outbox dispatcher (D3) polls on a 5s default interval (OutboxOptions.Interval) rather than pushing
        // synchronously from the join request, so this window must comfortably clear a full cycle plus dispatch.
        var lobbyChangedCompleted = await Task.WhenAny(lobbyChangedTcs.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        lobbyChangedCompleted.Should().Be(lobbyChangedTcs.Task,
            "SubscribeLobby must have taken effect before SendLobbyMessage is invoked");

        var clientCommandId = Guid.NewGuid();
        var sendResult = await connection.InvokeAsync<JsonElement>(
            "SendLobbyMessage", lobbyId, "hello from the hub happy path", clientCommandId);

        var sentMessage = sendResult.GetProperty("message");
        sentMessage.GetProperty("body").GetString().Should().Be("hello from the hub happy path");
        sentMessage.GetProperty("deleted").GetBoolean().Should().BeFalse();
        var sentMessageId = sentMessage.GetProperty("id").GetGuid();

        var chatCompleted = await Task.WhenAny(chatMessageTcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        chatCompleted.Should().Be(chatMessageTcs.Task, "the subscribed connection must receive ChatMessageCreated");

        var broadcastMessage = await chatMessageTcs.Task;
        broadcastMessage.GetProperty("id").GetGuid().Should().Be(sentMessageId);
        broadcastMessage.GetProperty("lobbyId").GetGuid().Should().Be(lobbyId);
        broadcastMessage.GetProperty("body").GetString().Should().Be("hello from the hub happy path");
        broadcastMessage.GetProperty("deleted").GetBoolean().Should().BeFalse();
        broadcastMessage.GetProperty("sender").GetProperty("userId").GetGuid().Should().NotBeEmpty();
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

    /// <summary>
    /// Registers + logs in a fresh account on the given client and returns a raw <c>Cookie</c> request-header
    /// value (e.g. <c>"access_token=...; refresh_token=..."</c>) built directly from the login response's
    /// Set-Cookie headers, for use as <see cref="HttpConnectionOptions.Headers"/>["Cookie"] on the hub connection.
    ///
    /// Deliberately NOT <see cref="HttpConnectionOptions.Cookies"/>: SignalR's client only applies that
    /// <see cref="CookieContainer"/> to the internal <see cref="HttpClientHandler"/> it constructs and passes
    /// *into* <see cref="HttpConnectionOptions.HttpMessageHandlerFactory"/> — a factory that (as ours does here,
    /// to route onto <see cref="Microsoft.AspNetCore.TestHost.TestServer"/>) returns a different handler entirely
    /// discards that CookieContainer silently (confirmed by a first run of this file: every connect attempt got a
    /// 401 on negotiate despite a populated CookieContainer). Setting the header directly is honored by every
    /// outgoing request the transport makes (negotiate, poll, send) — the same mechanism already proven to work
    /// for the Origin header below. Cookie values are JWTs (base64url), so no escaping is needed.
    /// </summary>
    private static async Task<string> SignInAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var email = $"rt-{suffix}@example.com";
        var username = $"rt{suffix}";

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

        var pairs = login.Headers.GetValues("Set-Cookie")
            .Select(setCookie => setCookie.Split(';')[0])
            .Where(nameValue => nameValue.IndexOf('=') > 0);
        return string.Join("; ", pairs);
    }

    private static async Task<Guid> CreatePrivateLobbyAsync(HttpClient host)
    {
        var response = await host.PostAsJsonAsync("/api/lobbies", new
        {
            GameSlug = "chess-lite",
            CapabilityVersion = 1,
            Privacy = "Private",
            MaxPlayers = 4,
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

    /// <summary>Unlike <see cref="CreatePrivateLobbyAsync"/>, produces a lobby a non-member can actually join via
    /// the REST join endpoint (<c>LobbiesService</c> only allows joining a lobby that is
    /// <c>Privacy == Public &amp;&amp; State == Open</c>) — needed so a second connection can legitimately become a
    /// member and observe its own LobbyChanged hint before SendLobbyMessage.</summary>
    private static async Task<Guid> CreatePublicLobbyAsync(HttpClient host)
    {
        var response = await host.PostAsJsonAsync("/api/lobbies", new
        {
            GameSlug = "chess-lite",
            CapabilityVersion = 1,
            Privacy = "Public",
            MaxPlayers = 4,
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
                minPlayers: 2, maxPlayers: 4, initialLifecycle: GameLifecycle.Available,
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
                "chess-lite", 1, minPlayers: 2, maxPlayers: 4,
                allowedModes: new[] { "multiplayer", "cooperative" },
                timeControls: new[] { "blitz-3-2", "rapid-10-0", "untimed" },
                tieBreakRules: new[] { "none", "sudden-death" },
                spectatorPolicies: new[] { "Anyone", "FriendsOnly", "Disabled" },
                ratedEligible: false, aiFillEligible: false,
                manifestVersion: "2026.1"));
            await db.SaveChangesAsync();
        }
    }

    private HubConnection CreateHubConnection(string cookieHeader, string origin) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri("https://localhost" + HubPath), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers["Origin"] = origin;
                options.Headers["Cookie"] = cookieHeader;
            })
            .Build();

    public void Dispose() => _factory.Dispose();
}
