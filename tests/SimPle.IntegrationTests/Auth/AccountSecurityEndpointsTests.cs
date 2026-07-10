using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SimPle.IntegrationTests.Auth;

/// <summary>
/// Integration tests for the account-security endpoints:
/// change-password, change-email, sessions, revoke-session, delete-account.
/// </summary>
public sealed class AccountSecurityEndpointsTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    // ── Change password ───────────────────────────────────────────────────────

    [Fact]
    public async Task ChangePassword_Unauthenticated_Returns401()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/change-password", new
        {
            CurrentPassword = "old-pw",
            NewPassword = "NewPassword1",
            ConfirmNewPassword = "NewPassword1"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_Returns401()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PostAsJsonAsync("/api/auth/change-password", new
        {
            CurrentPassword = "wrong-password",
            NewPassword = "NewPassword1",
            ConfirmNewPassword = "NewPassword1"
        });

        // Auth.InvalidCredentials maps to 401 to be consistent with the login flow.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Auth.InvalidCredentials");
    }

    [Fact]
    public async Task ChangePassword_ValidRequest_Returns204AndRevokesSession()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PostAsJsonAsync("/api/auth/change-password", new
        {
            CurrentPassword = TestPassword,
            NewPassword = "UpdatedPassword1",
            ConfirmNewPassword = "UpdatedPassword1"
        });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // After password change sessions are revoked — refresh should fail.
        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", (object?)null);
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ChangePassword_ValidationError_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        // Short password fails validator.
        var response = await client.PostAsJsonAsync("/api/auth/change-password", new
        {
            CurrentPassword = TestPassword,
            NewPassword = "short",
            ConfirmNewPassword = "short"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Change email ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangeEmail_Unauthenticated_Returns401()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/change-email",
            new { NewEmail = "new@example.com" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ChangeEmail_SameEmail_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PostAsJsonAsync("/api/auth/change-email",
            new { NewEmail = email });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Auth.EmailUnchanged");
    }

    [Fact]
    public async Task ChangeEmail_NewValidEmail_Returns204AndSendsVerification()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PostAsJsonAsync("/api/auth/change-email",
            new { NewEmail = "changed-unique@example.com" });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.EmailCapture.LastVerificationUrl.Should().NotBeNull()
            .And.Contain("/verify-email/confirm?token=");
    }

    // ── Active sessions ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetSessions_Unauthenticated_Returns401()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/auth/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetSessions_Authenticated_ReturnsSessionList()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.GetAsync("/api/auth/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"ipAddress\"");
        body.Should().Contain("\"expiresAt\"");
    }

    // ── Revoke session ────────────────────────────────────────────────────────

    [Fact]
    public async Task RevokeSession_InvalidId_Returns400OrNotFound()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.DeleteAsync($"/api/auth/sessions/{Guid.NewGuid()}");

        // Not found → mapped to 400 by the Failure helper.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RevokeSession_OwnSession_Returns204()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        // Get the session id.
        var sessionsResponse = await client.GetAsync("/api/auth/sessions");
        var sessionsBody = await sessionsResponse.Content.ReadAsStringAsync();

        // Parse out an id with a simple substring search (avoids heavyweight json dep).
        var idStart = sessionsBody.IndexOf("\"id\":\"", StringComparison.Ordinal) + 6;
        var idEnd = sessionsBody.IndexOf('"', idStart);
        var sessionId = sessionsBody[idStart..idEnd];

        var revoke = await client.DeleteAsync($"/api/auth/sessions/{sessionId}");

        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── Delete account ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAccount_Unauthenticated_Returns401()
    {
        using var client = CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/auth/account")
        {
            Content = JsonContent.Create(new { Password = "any" })
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteAccount_WrongPassword_Returns401()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/auth/account")
        {
            Content = JsonContent.Create(new { Password = "wrong-password" })
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Auth.InvalidCredentials");
    }

    [Fact]
    public async Task DeleteAccount_CorrectPassword_Returns204AndAccountIsGone()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/auth/account")
        {
            Content = JsonContent.Create(new { Password = TestPassword })
        });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Account gone — login should fail.
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            EmailOrUsername = email,
            Password = TestPassword,
            CaptchaToken = "test-captcha-token"
        });
        login.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Account deletion cascade with friends & blocks ───────────────────────

    [Fact]
    public async Task DeleteAccount_WithFriendshipsAndBlocks_OtherUsersUnaffected()
    {
        // Register three users: A (to be deleted), B, and C.
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        using var clientC = CreateClient();
        var (emailC, usernameC) = UniqueUser();
        await RegisterAndLoginAsync(clientC, emailC, usernameC);

        async Task<Guid> GetUserId(HttpClient from, string targetUsername)
        {
            var r = await from.GetAsync($"/api/profile/{targetUsername}");
            var j = System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            return j.RootElement.GetProperty("userId").GetGuid();
        }

        async Task<Guid> GetIncomingRequestId(HttpClient addresseeClient, string requesterUsername)
        {
            var r = await addresseeClient.GetAsync("/api/friends/requests?direction=incoming");
            var j = System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var items = j.RootElement.GetProperty("items");
            for (int i = 0; i < items.GetArrayLength(); i++)
            {
                var item = items[i];
                if (item.GetProperty("requesterUsername").GetString() == requesterUsername)
                    return item.GetProperty("requestId").GetGuid();
            }
            throw new InvalidOperationException($"No incoming request from {requesterUsername}");
        }

        var userBId = await GetUserId(clientA, usernameB);
        var userCId = await GetUserId(clientA, usernameC);

        // A → B: A is RequesterId; B accepts as AddresseeId
        await clientA.PostAsJsonAsync("/api/friends/requests", new { TargetUserId = userBId });
        var reqAB = await GetIncomingRequestId(clientB, usernameA);
        await clientB.PostAsJsonAsync($"/api/friends/requests/{reqAB}/accept", (object?)null);

        // B → C: B is RequesterId; C accepts as AddresseeId (tests AddresseeId cascade role)
        var userCIdFromB = await GetUserId(clientB, usernameC);
        await clientB.PostAsJsonAsync("/api/friends/requests", new { TargetUserId = userCIdFromB });
        var reqBC = await GetIncomingRequestId(clientC, usernameB);
        await clientC.PostAsJsonAsync($"/api/friends/requests/{reqBC}/accept", (object?)null);

        // A blocks C (A is BlockerId)
        await clientA.PostAsJsonAsync("/api/friends/blocks", new { TargetUserId = userCId });

        // A sets custom privacy (exercises user_friend_settings row creation)
        await clientA.PutAsJsonAsync("/api/friends/settings", new { FriendRequestPrivacy = "FriendsOfFriends" });

        // Delete A's account
        var deleteResp = await clientA.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/auth/account")
        {
            Content = JsonContent.Create(new { Password = TestPassword })
        });
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // A's profile is gone — 404
        using var anonClient = CreateClient();
        (await anonClient.GetAsync($"/api/profile/{usernameA}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        // B and C still exist — their profile pages return 200
        (await anonClient.GetAsync($"/api/profile/{usernameB}")).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await anonClient.GetAsync($"/api/profile/{usernameC}")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // B can still access C's profile, confirming the B–C friendship edge is intact
        (await clientB.GetAsync($"/api/profile/{usernameC}")).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private const string TestPassword = "ValidPassword1";

    // Each test call gets a unique email/username to avoid cross-test interference.
    private static (string email, string username) UniqueUser()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return ($"sec-{suffix}@example.com", $"sec{suffix}");
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

internal static class HttpClientExtensions
{
    internal static HttpClient WithCsrfHeader(this HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        return client;
    }
}
