using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using SimPle.Application.Auth.DTOs;

namespace SimPle.IntegrationTests.Auth;

public sealed class AuthEndpointsTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    // ── Registration ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Register_ReturnsCreated_AndDuplicateIsSafeConflict()
    {
        using var client = CreateClient();

        var created = await RegisterAsync(client);
        var duplicate = await RegisterAsync(client);
        var json = await duplicate.Content.ReadAsStringAsync();

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        json.Should().Contain("Auth.EmailTaken").And.NotContain("PasswordHash");
    }

    [Fact]
    public async Task Register_SendsVerificationEmail()
    {
        using var client = CreateClient();

        await RegisterAsync(client);

        _factory.EmailCapture.LastVerificationUrl.Should().NotBeNull()
            .And.Contain("/verify-email/confirm?token=");
    }

    [Fact]
    public async Task Register_ReturnsUserWithEmailVerifiedFalse()
    {
        using var client = CreateClient();

        var response = await RegisterAsync(client);
        var json = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        json.Should().Contain("\"isEmailVerified\":false");
    }

    // ── Login ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoginSetsHttpOnlyCookies_AndMeReturnsSafeUser()
    {
        using var client = CreateClient();
        await RegisterAsync(client);

        var login = await LoginAsync(client);
        var cookies = login.Headers.GetValues("Set-Cookie").ToArray();
        var me = await client.GetAsync("/api/auth/me");
        var json = await me.Content.ReadAsStringAsync();

        login.StatusCode.Should().Be(HttpStatusCode.OK);
        cookies.Should().Contain(cookie => cookie.StartsWith("access_token=") && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        cookies.Should().Contain(cookie => cookie.StartsWith("refresh_token=") && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        json.Should().Contain("mohan@example.com");
        json.ToLowerInvariant().Should().NotContain("password")
            .And.NotContain("refresh")
            .And.NotContain("access_token");
    }

    [Fact]
    public async Task MeRequiresAuthentication()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Auth.Unauthorized");
    }

    [Fact]
    public async Task LoginFailure_IsUnauthorizedWithoutSensitiveDetail()
    {
        using var client = CreateClient();
        await RegisterAsync(client);

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            EmailOrUsername = "mohan@example.com",
            Password = "incorrect password",
            CaptchaToken = "test-captcha-token"
        });
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        body.Should().Contain("Invalid email/username or password.")
            .And.NotContain("PasswordHash");
    }

    // ── Refresh / Logout ──────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshRotatesCookies_AndLogoutClearsCookies()
    {
        using var client = CreateClient();
        await RegisterAsync(client);
        var login = await LoginAsync(client);
        var firstRefresh = login.Headers.GetValues("Set-Cookie")
            .Single(cookie => cookie.StartsWith("refresh_token="));

        var refresh = await client.PostAsync("/api/auth/refresh", null);
        var secondRefresh = refresh.Headers.GetValues("Set-Cookie")
            .Single(cookie => cookie.StartsWith("refresh_token="));
        var logout = await client.PostAsync("/api/auth/logout", null);
        var cleared = logout.Headers.GetValues("Set-Cookie").ToArray();

        refresh.StatusCode.Should().Be(HttpStatusCode.OK);
        secondRefresh.Should().NotBe(firstRefresh);
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        cleared.Should().Contain(cookie => cookie.StartsWith("access_token=") && cookie.Contains("expires=", StringComparison.OrdinalIgnoreCase));
        cleared.Should().Contain(cookie => cookie.StartsWith("refresh_token=") && cookie.Contains("expires=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReusingRotatedRefreshToken_InvalidatesTheSessionFamily()
    {
        using var client = CreateClient();
        await RegisterAsync(client);
        var login = await LoginAsync(client);
        var oldCookie = login.Headers.GetValues("Set-Cookie")
            .Single(cookie => cookie.StartsWith("refresh_token="))
            .Split(';')[0];

        (await client.PostAsync("/api/auth/refresh", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var replayClient = CreateClient();
        replayClient.DefaultRequestHeaders.Add("Cookie", oldCookie);
        var replay = await replayClient.PostAsync("/api/auth/refresh", null);

        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await replay.Content.ReadAsStringAsync()).Should().Contain("Auth.TokenReuse");
    }

    [Fact]
    public async Task LogoutAllRevokesRefreshSessionAndClearsCookies()
    {
        using var client = CreateClient();
        await RegisterAsync(client);
        await LoginAsync(client);

        var logoutAll = await client.PostAsync("/api/auth/logout-all", null);
        var refresh = await client.PostAsync("/api/auth/refresh", null);

        logoutAll.StatusCode.Should().Be(HttpStatusCode.NoContent);
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── CSRF guard ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostEndpointsRequireCsrfHeader()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.PostAsJsonAsync("/api/auth/register", RegisterRequest());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Auth.CsrfHeaderRequired");
    }

    [Fact]
    public async Task VerifyEmail_RequiresCsrfHeader()
    {
        using var client = CreateClientWithoutCsrf();

        var response = await client.PostAsJsonAsync("/api/auth/verify-email", new { Token = "some-token" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Auth.CsrfHeaderRequired");
    }

    [Fact]
    public async Task ForgotPassword_RequiresCsrfHeader()
    {
        using var client = CreateClientWithoutCsrf();

        var response = await client.PostAsJsonAsync("/api/auth/forgot-password", new { Email = "test@example.com" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Auth.CsrfHeaderRequired");
    }

    [Fact]
    public async Task ResetPassword_RequiresCsrfHeader()
    {
        using var client = CreateClientWithoutCsrf();

        var response = await client.PostAsJsonAsync("/api/auth/reset-password", new
        {
            Token = "some-token",
            NewPassword = "ValidPass1!",
            ConfirmNewPassword = "ValidPass1!"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Auth.CsrfHeaderRequired");
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RegisterValidation_ReturnsBadRequestForWeakPassword()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Username = "mohan",
            Email = "mohan@example.com",
            Password = "short",
            ConfirmPassword = "short",
            CaptchaToken = "test-captcha-token"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Captcha ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Register_RejectsFailedCaptchaBeforeCreatingAccount()
    {
        using var factory = new TestWebApplicationFactory(captchaSucceeds: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.PostAsJsonAsync("/api/auth/register", RegisterRequest());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Auth.CaptchaFailed");
    }

    [Fact]
    public async Task Login_RejectsFailedCaptchaBeforeCheckingCredentials()
    {
        using var factory = new TestWebApplicationFactory(captchaSucceeds: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            EmailOrUsername = "nobody@example.com",
            Password = "not-used-because-captcha-fails",
            CaptchaToken = "test-captcha-token"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Auth.CaptchaFailed");
    }

    // ── Check email ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckEmail_Returns204WhenEmailAvailable()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/auth/check-email?email=new@example.com");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CheckEmail_Returns409WhenEmailAlreadyTaken()
    {
        using var client = CreateClient();
        await RegisterAsync(client);

        var response = await client.GetAsync("/api/auth/check-email?email=mohan@example.com");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Auth.EmailTaken");
    }

    [Fact]
    public async Task CheckEmail_Returns400WhenEmailParameterMissing()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/auth/check-email");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Verify email ──────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyEmail_ReturnsBadRequest_ForInvalidToken()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/verify-email",
            new { Token = "bad-token-that-does-not-exist" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Auth.InvalidToken");
    }

    [Fact]
    public async Task VerifyEmail_ReturnsNoContent_ForValidToken()
    {
        using var client = CreateClient();
        await RegisterAsync(client);
        var token = _factory.GetLastVerificationToken();

        token.Should().NotBeNullOrEmpty("a verification email should have been captured");
        var response = await client.PostAsJsonAsync("/api/auth/verify-email", new { Token = token });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task VerifyEmail_UpdatesEmailVerifiedStatus()
    {
        using var client = CreateClient();
        await RegisterAsync(client);
        await LoginAsync(client);
        var token = _factory.GetLastVerificationToken();

        await client.PostAsJsonAsync("/api/auth/verify-email", new { Token = token });

        var me = await client.GetAsync("/api/auth/me");
        var json = await me.Content.ReadAsStringAsync();
        json.Should().Contain("\"isEmailVerified\":true");
    }

    [Fact]
    public async Task VerifyEmail_SendsWelcomeEmailOnSuccess()
    {
        using var client = CreateClient();
        await RegisterAsync(client);
        var token = _factory.GetLastVerificationToken();

        await client.PostAsJsonAsync("/api/auth/verify-email", new { Token = token });

        _factory.EmailCapture.WelcomeEmailCount.Should().Be(1);
    }

    [Fact]
    public async Task VerifyEmail_ReturnsBadRequest_ForAlreadyUsedToken()
    {
        using var client = CreateClient();
        await RegisterAsync(client);
        var token = _factory.GetLastVerificationToken();
        await client.PostAsJsonAsync("/api/auth/verify-email", new { Token = token });

        var second = await client.PostAsJsonAsync("/api/auth/verify-email", new { Token = token });

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Resend verification ───────────────────────────────────────────────────

    [Fact]
    public async Task ResendVerification_RequiresAuthentication()
    {
        using var client = CreateClient();

        var response = await client.PostAsync("/api/auth/resend-verification", null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ResendVerification_Returns400_WhenCooldownActive()
    {
        // After registration a verification token is created (CreatedAt ≈ now).
        // Calling resend within 60 seconds should hit the cooldown.
        using var client = CreateClient();
        await RegisterAsync(client);
        await LoginAsync(client);

        var response = await client.PostAsync("/api/auth/resend-verification", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Auth.ResendTooSoon");
    }

    [Fact]
    public async Task ResendVerification_Returns204_WhenAlreadyVerified()
    {
        // Already-verified users get a silent 204 instead of an error so the frontend
        // can redirect them to the dashboard without extra error handling.
        using var client = CreateClient();
        await RegisterAsync(client);
        await LoginAsync(client);
        var token = _factory.GetLastVerificationToken();
        await client.PostAsJsonAsync("/api/auth/verify-email", new { Token = token });

        var response = await client.PostAsync("/api/auth/resend-verification", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── Forgot password ───────────────────────────────────────────────────────

    [Fact]
    public async Task ForgotPassword_Returns204_ForUnknownEmail()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/forgot-password",
            new { Email = "ghost@example.com" });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ForgotPassword_Returns204_AndCapturesResetUrl_ForKnownEmail()
    {
        using var client = CreateClient();
        await RegisterAsync(client);

        var response = await client.PostAsJsonAsync("/api/auth/forgot-password",
            new { Email = "mohan@example.com" });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.EmailCapture.LastResetUrl.Should().NotBeNull()
            .And.Contain("/reset-password?token=");
    }

    [Fact]
    public async Task ForgotPassword_ReturnsBadRequest_ForInvalidEmailFormat()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/forgot-password",
            new { Email = "not-an-email" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Reset password ────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetPassword_ReturnsBadRequest_ForInvalidToken()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/reset-password", new
        {
            Token = "bad-token-that-does-not-exist",
            NewPassword = "NewPass1!",
            ConfirmNewPassword = "NewPass1!"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Auth.InvalidToken");
    }

    [Fact]
    public async Task ResetPassword_ReturnsBadRequest_ForWeakPassword()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/reset-password", new
        {
            Token = "some-token",
            NewPassword = "weak",
            ConfirmNewPassword = "weak"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResetPassword_Returns204_ForValidToken()
    {
        using var client = CreateClient();
        await RegisterAsync(client);
        await client.PostAsJsonAsync("/api/auth/forgot-password",
            new { Email = "mohan@example.com" });
        var resetToken = _factory.GetLastResetToken();

        resetToken.Should().NotBeNullOrEmpty("a reset email should have been captured");
        var response = await client.PostAsJsonAsync("/api/auth/reset-password", new
        {
            Token = resetToken,
            NewPassword = "BrandNewPass1!",
            ConfirmNewPassword = "BrandNewPass1!"
        });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ResetPassword_SendsPasswordChangedNotification()
    {
        using var client = CreateClient();
        await RegisterAsync(client);
        await client.PostAsJsonAsync("/api/auth/forgot-password",
            new { Email = "mohan@example.com" });
        var resetToken = _factory.GetLastResetToken();

        await client.PostAsJsonAsync("/api/auth/reset-password", new
        {
            Token = resetToken,
            NewPassword = "BrandNewPass1!",
            ConfirmNewPassword = "BrandNewPass1!"
        });

        _factory.EmailCapture.PasswordChangedEmailCount.Should().Be(1);
    }

    [Fact]
    public async Task ResetPassword_AllowsLoginWithNewPassword()
    {
        using var client = CreateClient();
        await RegisterAsync(client);
        await client.PostAsJsonAsync("/api/auth/forgot-password",
            new { Email = "mohan@example.com" });
        var resetToken = _factory.GetLastResetToken();
        await client.PostAsJsonAsync("/api/auth/reset-password", new
        {
            Token = resetToken,
            NewPassword = "BrandNewPass1!",
            ConfirmNewPassword = "BrandNewPass1!"
        });

        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            EmailOrUsername = "mohan@example.com",
            Password = "BrandNewPass1!",
            CaptchaToken = "test-captcha-token"
        });

        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Rate limiting ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RegisterRateLimit_ReturnsTooManyRequestsAfterLimit()
    {
        using var client = CreateClient();
        HttpResponseMessage? response = null;

        for (var i = 0; i < 4; i++)
        {
            response = await client.PostAsJsonAsync("/api/auth/register", new
            {
                Username = "user" + i,
                Email = "user" + i + "@example.com",
                Password = "valid password phrase " + i,
                ConfirmPassword = "valid password phrase " + i,
                CaptchaToken = "test-captcha-token"
            });
        }

        response!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        // Module 3 reconciliation R12: the global OnRejected handler now emits the canonical
        // catalogue code RateLimit.Exceeded (was the per-endpoint Auth.RateLimitExceeded).
        (await response.Content.ReadAsStringAsync()).Should().Contain("RateLimit.Exceeded");
    }

    // ── Swagger ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Swagger_DescribesCookieAuthCsrfAndCoreEndpoints()
    {
        using var client = CreateClient();

        var document = await client.GetStringAsync("/swagger/v1/swagger.json");

        document.Should().Contain("\"/api/auth/register\"")
            .And.Contain("\"/api/auth/login\"")
            .And.Contain("\"/api/auth/refresh\"")
            .And.Contain("\"/api/auth/logout-all\"")
            .And.Contain("\"/api/auth/me\"")
            .And.Contain("\"/api/auth/verify-email\"")
            .And.Contain("\"/api/auth/forgot-password\"")
            .And.Contain("\"/api/auth/reset-password\"")
            .And.Contain("\"accessCookie\"")
            .And.Contain("\"csrfHeader\"")
            .And.Contain("Auth_Login")
            .And.Contain("HttpOnly")
            .And.Contain("captchaToken")
            .And.Contain("reCAPTCHA v2")
            .And.Contain("\"minLength\": 8")
            .And.Contain("Safe authenticated-user response");
    }

    // ── Google OAuth ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Google_Returns401_WhenTokenIsInvalidOrUnknown()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/google",
            new { IdToken = "not-a-real-google-token" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Auth.InvalidGoogleToken");
    }

    [Fact]
    public async Task Google_Returns400_WhenCsrfHeaderIsMissing()
    {
        using var client = CreateClientWithoutCsrf();

        var response = await client.PostAsJsonAsync("/api/auth/google",
            new { IdToken = "some-token" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Google_ProvisionesNewUserAndSetsAuthCookies()
    {
        _factory.GoogleValidator.SetupValid("valid-google-token", FakeGoogleProfile());
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/google",
            new { IdToken = "valid-google-token" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("google@example.com")
            .And.Contain("\"isEmailVerified\":true")
            .And.NotContain("PasswordHash");

        // The auth cookies must be set on the response.
        var cookies = response.Headers
            .Where(h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value)
            .ToList();
        cookies.Should().Contain(c => c.StartsWith("access_token="));
        cookies.Should().Contain(c => c.StartsWith("refresh_token="));
    }

    [Fact]
    public async Task Google_ReturnsSameUserOnSecondCallWithSameToken()
    {
        _factory.GoogleValidator.SetupValid("valid-google-token", FakeGoogleProfile());
        using var client = CreateClient();

        // First call provisions the account.
        var firstResponse = await client.PostAsJsonAsync("/api/auth/google",
            new { IdToken = "valid-google-token" });
        var firstBody = await firstResponse.Content.ReadAsStringAsync();

        // Second call signs in the existing account (should not create a duplicate).
        var secondResponse = await client.PostAsJsonAsync("/api/auth/google",
            new { IdToken = "valid-google-token" });
        var secondBody = await secondResponse.Content.ReadAsStringAsync();

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Same email address — same logical user.
        firstBody.Should().Contain("google@example.com");
        secondBody.Should().Contain("google@example.com");
    }

    [Fact]
    public async Task Google_LinksGoogleIdToExistingPasswordAccount()
    {
        // Provision a regular password account first.
        using var client = CreateClient();
        await RegisterAsync(client); // creates mohan@example.com

        // Now sign in with Google using the same email.
        _factory.GoogleValidator.SetupValid("link-token", new GoogleUserInfo(
            GoogleId: "google-link-uid",
            Email: "mohan@example.com",
            DisplayName: "Mohan Ehab",
            GivenName: "Mohan",
            PictureUrl: null,
            EmailVerified: true));

        var response = await client.PostAsJsonAsync("/api/auth/google",
            new { IdToken = "link-token" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("mohan@example.com");
    }

    [Fact]
    public async Task Google_NewUserEmailIsAlreadyVerified()
    {
        _factory.GoogleValidator.SetupValid("new-user-token", FakeGoogleProfile());
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/google",
            new { IdToken = "new-user-token" });

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"isEmailVerified\":true");
    }

    private static GoogleUserInfo FakeGoogleProfile() => new(
        GoogleId: "google-test-uid-999",
        Email: "google@example.com",
        DisplayName: "Google User",
        GivenName: "Google",
        PictureUrl: "https://lh3.googleusercontent.com/a/test",
        EmailVerified: true);

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private HttpClient CreateClientWithoutCsrf() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true
        });

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/auth/register", RegisterRequest());

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/auth/login", new
        {
            EmailOrUsername = "mohan@example.com",
            Password = "valid password phrase",
            CaptchaToken = "test-captcha-token"
        });

    private static object RegisterRequest() => new
    {
        Username = "mohan",
        Email = "mohan@example.com",
        Password = "valid password phrase",
        ConfirmPassword = "valid password phrase",
        CaptchaToken = "test-captcha-token"
    };

    public void Dispose() => _factory.Dispose();
}
