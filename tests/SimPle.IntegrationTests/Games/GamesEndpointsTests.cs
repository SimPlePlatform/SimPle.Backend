using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SimPle.Application.Games.DTOs;
using SimPle.Domain.Games;
using SimPle.Infrastructure.Persistence;
using SimPle.IntegrationTests.Auth;

namespace SimPle.IntegrationTests.Games;

/// <summary>
/// HTTP-contract tests for the Module 4 catalog/favorites endpoints (InMemory-backed
/// <see cref="TestWebApplicationFactory"/>): anonymous public reads, 404/410 lifecycle semantics, ETag/304/
/// Vary caching, favorite idempotency/ownership/CSRF, and rate limits. Games have no create-via-API path (they
/// are seeded by <c>GameCatalogSeeder</c>/an admin CLI, not user input), so tests seed rows directly through the
/// DI-registered <see cref="AppDbContext"/>. Real-PostgreSQL-only concerns (xmin concurrency, 23505 favorite
/// races, EXPLAIN/index usage, LIKE-wildcard escaping) live in <c>GamesPostgresConcurrencyTests</c> instead.
/// </summary>
public sealed class GamesEndpointsTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    // ── Anonymous public reads ──────────────────────────────────────────────

    [Fact]
    public async Task List_Anonymous_Returns200WithPublicLifecyclesOnly()
    {
        using var client = CreateClient();
        await SeedGameAsync("chess-lite", GameLifecycle.Available);
        await SeedGameAsync("hidden-draft", GameLifecycle.Draft);

        var response = await client.GetAsync("/api/games");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("chess-lite");
        body.Should().NotContain("hidden-draft");
    }

    [Fact]
    public async Task List_Anonymous_HasPublicCacheHeadersAndETag()
    {
        using var client = CreateClient();
        await SeedGameAsync("chess-lite", GameLifecycle.Available);

        var response = await client.GetAsync("/api/games");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag.Should().NotBeNull();
        response.Headers.CacheControl!.ToString().Should().Contain("public");
        response.Headers.Vary.Should().Contain("Cookie");
    }

    [Fact]
    public async Task GetBySlug_Anonymous_Available_Returns200()
    {
        using var client = CreateClient();
        await SeedGameAsync("chess-lite", GameLifecycle.Available);

        var response = await client.GetAsync("/api/games/chess-lite");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetBySlug_UnknownSlug_Returns404()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/games/no-such-game");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Games.NotFound");
    }

    [Fact]
    public async Task GetBySlug_DraftGame_Returns404_IndistinguishableFromUnknown()
    {
        using var client = CreateClient();
        await SeedGameAsync("draft-game", GameLifecycle.Draft);

        var response = await client.GetAsync("/api/games/draft-game");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetBySlug_RetiredGame_Returns410WithTombstoneDto_NoETagOrCacheHeaders()
    {
        using var client = CreateClient();
        await SeedGameAsync("retired-game", GameLifecycle.Retired);

        var response = await client.GetAsync("/api/games/retired-game");

        response.StatusCode.Should().Be((HttpStatusCode)410);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("retired-game");
        body.Should().Contain("Games.Retired");
        response.Headers.ETag.Should().BeNull();
        response.Headers.CacheControl.Should().BeNull();
    }

    [Fact]
    public async Task GetFeatured_NoneFeatured_Returns204()
    {
        using var client = CreateClient();
        await SeedGameAsync("chess-lite", GameLifecycle.Available);

        var response = await client.GetAsync("/api/games/featured");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task GetFeatured_OneFeatured_Returns200WithETag()
    {
        using var client = CreateClient();
        await SeedGameAsync("chess-lite", GameLifecycle.Available, featuredRank: 1);

        var response = await client.GetAsync("/api/games/featured");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.ETag.Should().NotBeNull();
    }

    // ── ETag / 304 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetBySlug_IfNoneMatchWithCurrentETag_Returns304()
    {
        using var client = CreateClient();
        await SeedGameAsync("chess-lite", GameLifecycle.Available);

        var first = await client.GetAsync("/api/games/chess-lite");
        var etag = first.Headers.ETag!.Tag;

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/games/chess-lite");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var second = await client.SendAsync(request);

        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task GetBySlug_IfNoneMatchWithStaleETag_Returns200()
    {
        using var client = CreateClient();
        await SeedGameAsync("chess-lite", GameLifecycle.Available);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/games/chess-lite");
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"stale-etag-value\"");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Search / filter / cursor validation ──────────────────────────────────

    [Fact]
    public async Task List_SearchTooShort_Returns400ValidationFailed()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/games?query=a");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Validation.Failed");
    }

    [Fact]
    public async Task List_UnknownCategoryFilter_Returns400ValidationFailed()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/games?category=not-a-real-category");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_InvalidCursor_Returns400InvalidCursor()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/games?after=@@not-a-cursor@@");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Pagination.InvalidCursor");
    }

    [Fact]
    public async Task List_PageSizeOverMax_Returns400ValidationFailed()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/games?limit=51");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Favorites: auth + CSRF guards ────────────────────────────────────────

    [Fact]
    public async Task GetFavorites_Unauthenticated_Returns401()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/games/me/favorites");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PutFavorite_Unauthenticated_Returns401()
    {
        using var client = CreateClient();

        var response = await client.PutAsync("/api/games/me/favorites/chess-lite", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PutFavorite_MissingCsrfHeader_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);
        await SeedGameAsync("chess-lite", GameLifecycle.Available);
        client.DefaultRequestHeaders.Remove("X-Requested-With");

        var response = await client.PutAsync("/api/games/me/favorites/chess-lite", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Auth.CsrfHeaderRequired");
    }

    [Fact]
    public async Task DeleteFavorite_MissingCsrfHeader_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);
        await SeedGameAsync("chess-lite", GameLifecycle.Available);
        client.DefaultRequestHeaders.Remove("X-Requested-With");

        var response = await client.DeleteAsync("/api/games/me/favorites/chess-lite");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Favorites: happy path + idempotency + ownership ──────────────────────

    [Fact]
    public async Task PutFavorite_UnknownSlug_Returns404()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PutAsync("/api/games/me/favorites/no-such-game", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PutFavorite_NewFavorite_Returns200_NeverCached()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);
        await SeedGameAsync("chess-lite", GameLifecycle.Available);

        var response = await client.PutAsync("/api/games/me/favorites/chess-lite", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.ToString().Should().Contain("no-store");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("chess-lite");
    }

    [Fact]
    public async Task PutFavorite_CalledTwice_IsIdempotent_SameDto()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);
        await SeedGameAsync("chess-lite", GameLifecycle.Available);

        var first = await client.PutAsync("/api/games/me/favorites/chess-lite", null);
        var second = await client.PutAsync("/api/games/me/favorites/chess-lite", null);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.Content.ReadAsStringAsync()).Should().Be(await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PutFavorite_RetiredGame_Returns409GamesRetired()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);
        await SeedGameAsync("retired-game", GameLifecycle.Retired);

        var response = await client.PutAsync("/api/games/me/favorites/retired-game", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Games.Retired");
    }

    [Fact]
    public async Task DeleteFavorite_ThenGetFavorites_NoLongerListed()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);
        await SeedGameAsync("chess-lite", GameLifecycle.Available);

        await client.PutAsync("/api/games/me/favorites/chess-lite", null);
        var deleteResponse = await client.DeleteAsync("/api/games/me/favorites/chess-lite");
        var listResponse = await client.GetAsync("/api/games/me/favorites");

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var body = await listResponse.Content.ReadAsStringAsync();
        body.Should().NotContain("chess-lite");
    }

    [Fact]
    public async Task DeleteFavorite_NeverFavorited_IsIdempotent_Returns204()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);
        await SeedGameAsync("chess-lite", GameLifecycle.Available);

        var response = await client.DeleteAsync("/api/games/me/favorites/chess-lite");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task GetFavorites_OnlyShowsCallersOwnFavorites_NotAnotherUsersFavorite()
    {
        using var owner = CreateClient();
        var (ownerEmail, ownerUsername) = UniqueUser();
        await RegisterAndLoginAsync(owner, ownerEmail, ownerUsername);
        await SeedGameAsync("chess-lite", GameLifecycle.Available);
        await owner.PutAsync("/api/games/me/favorites/chess-lite", null);

        using var stranger = CreateClient();
        var (strangerEmail, strangerUsername) = UniqueUser();
        await RegisterAndLoginAsync(stranger, strangerEmail, strangerUsername);

        var response = await stranger.GetAsync("/api/games/me/favorites");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("chess-lite");
    }

    // ── Rate limits ───────────────────────────────────────────────────────────

    [Fact]
    public async Task List_CatalogSearchRateLimit_ReturnsTooManyRequestsAfterLimit()
    {
        // catalog-search GlobalLimiter branch: 30/min/IP, only when `query` is non-empty (Program.cs).
        using var client = CreateClient();

        HttpResponseMessage? response = null;
        for (var i = 0; i < 31; i++)
            response = await client.GetAsync("/api/games?query=chess");

        response!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await response.Content.ReadAsStringAsync()).Should().Contain("RateLimit.Exceeded");
        response.Headers.Contains("Retry-After").Should().BeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private const string TestPassword = "ValidPassword1";

    private static (string email, string username) UniqueUser()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return ($"gme-{suffix}@example.com", $"gme{suffix}");
    }

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true
        }).WithCsrfHeader();

    private static async Task RegisterAndLoginAsync(HttpClient client, string email, string username)
    {
        await client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = username,
            Email = email,
            Password = TestPassword,
            ConfirmPassword = TestPassword,
            CaptchaToken = "test-captcha-token"
        });
        await client.PostAsJsonAsync("/api/auth/login", new
        {
            EmailOrUsername = email,
            Password = TestPassword,
            CaptchaToken = "test-captcha-token"
        });
    }

    private async Task SeedGameAsync(string slug, GameLifecycle lifecycle, int? featuredRank = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Games.Add(Game.Create(
            slug, "Test Game " + slug, "A test game summary.", "Test rules summary.", GameDifficulty.Medium,
            5, 25, 2, 2, lifecycle, featuredRank, 0,
            "art-token", "#111111", "#222222", "Test game abstract artwork", "2026.1",
            "strategy", Array.Empty<string>(), Array.Empty<string>()));
        await db.SaveChangesAsync();
    }

    public void Dispose() => _factory.Dispose();
}
