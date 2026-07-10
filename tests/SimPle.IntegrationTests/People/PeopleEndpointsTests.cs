using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using SimPle.IntegrationTests.Auth;

namespace SimPle.IntegrationTests.People;

public sealed class PeopleEndpointsTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    // ── Auth guard ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_Unauthenticated_Returns401()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/people/search?q=carol");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_QueryTooShort_Returns400ValidationFailed()
    {
        // A non-empty, non-whitespace 1-char query passes ASP.NET's implicit [Required] model-binding check
        // (which rejects null/empty/whitespace-only before the action runs) and reaches the service's own
        // 2-100 char length validation.
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.GetAsync("/api/people/search?q=a");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Validation.Failed");
    }

    [Fact]
    public async Task Search_MissingQueryParam_Returns400()
    {
        // With q entirely absent, ASP.NET's implicit [ApiController] model-binding validation rejects the
        // request before the controller's own IsNullOrWhiteSpace check runs; still a 400, different body shape.
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.GetAsync("/api/people/search");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_InvalidCursor_Returns400InvalidCursor()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.GetAsync("/api/people/search?q=carol&cursor=@@not-a-cursor@@");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Pagination.InvalidCursor");
    }

    // ── Happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_FindsRegisteredUserByUsernamePrefix_Returns200WithResult()
    {
        using var seeker = CreateClient();
        var (seekerEmail, seekerUsername) = UniqueUser();
        await RegisterAndLoginAsync(seeker, seekerEmail, seekerUsername);

        using var target = CreateClient();
        var (targetEmail, targetUsername) = UniqueUser();
        await RegisterAndLoginAsync(target, targetEmail, targetUsername);

        var response = await seeker.GetAsync($"/api/people/search?q={targetUsername}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(targetUsername);
        response.Headers.CacheControl!.ToString().Should().Contain("no-store");
    }

    [Fact]
    public async Task Search_AtPrefixIsStripped_StillMatches()
    {
        using var seeker = CreateClient();
        var (seekerEmail, seekerUsername) = UniqueUser();
        await RegisterAndLoginAsync(seeker, seekerEmail, seekerUsername);

        using var target = CreateClient();
        var (targetEmail, targetUsername) = UniqueUser();
        await RegisterAndLoginAsync(target, targetEmail, targetUsername);

        var response = await seeker.GetAsync($"/api/people/search?q=@{targetUsername}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(targetUsername);
    }

    // ── Abuse limits ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_RateLimit_ReturnsTooManyRequestsAfterLimit()
    {
        // people-search policy: 30/min per authenticated account (Program.cs FriendWindow "psrc").
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        HttpResponseMessage? response = null;
        for (var i = 0; i < 31; i++)
            response = await client.GetAsync("/api/people/search?q=carol");

        response!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await response.Content.ReadAsStringAsync()).Should().Contain("RateLimit.Exceeded");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private const string TestPassword = "ValidPassword1";

    private static (string email, string username) UniqueUser()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return ($"ppl-{suffix}@example.com", $"ppl{suffix}");
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

    public void Dispose() => _factory.Dispose();
}
