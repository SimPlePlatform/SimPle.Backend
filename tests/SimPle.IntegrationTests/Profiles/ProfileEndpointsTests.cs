using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using SimPle.IntegrationTests.Auth;

namespace SimPle.IntegrationTests.Profiles;

public sealed class ProfileEndpointsTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    // ── /api/profile/me ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetMe_Unauthenticated_Returns401()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/profile/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMe_AfterRegister_ReturnsProfileWithCorrectFields()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.GetAsync("/api/profile/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain($"\"username\":\"{username}\"");
        body.Should().Contain("\"visibility\":\"Public\"");
        body.Should().Contain("\"profileType\":\"Player\"");
        body.Should().NotContain("email");
        body.Should().NotContain("passwordHash");
        body.Should().NotContain("failedLoginCount");
    }

    // ── PUT /api/profile/me ────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateMe_Unauthenticated_Returns401()
    {
        using var client = CreateClient();

        var response = await client.PutAsJsonAsync("/api/profile/me", new
        {
            DisplayName = "Test",
            Visibility = "Public"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UpdateMe_ValidRequest_Returns200WithUpdatedData()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PutAsJsonAsync("/api/profile/me", new
        {
            DisplayName = "My Updated Name",
            Bio = "Hello from integration test",
            Region = "NA-East",
            StatusMessage = "Testing!",
            Visibility = "Private",
            ProfileType = "Developer"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("My Updated Name");
        body.Should().Contain("Hello from integration test");
        body.Should().Contain("NA-East");
        body.Should().Contain("Private");
        body.Should().Contain("Developer");
        body.Should().Contain("\"role\":\"Player\"");
    }

    [Fact]
    public async Task AvatarUploadUrl_Unauthenticated_Returns401()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/profile/me/avatar/upload-url", new
        {
            FileName = "avatar.png",
            ContentType = "image/png",
            FileSizeBytes = 1024
        });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AvatarUploadUrl_ValidRequest_ReturnsUserScopedObjectKey()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PostAsJsonAsync("/api/profile/me/avatar/upload-url", new
        {
            FileName = "avatar.png",
            ContentType = "image/png",
            FileSizeBytes = 1024
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("/avatar/");
        body.Should().Contain(".png");
        body.Should().Contain("uploadUrl");
    }

    [Fact]
    public async Task BannerUploadUrl_ValidRequest_ReturnsUserScopedObjectKey()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PostAsJsonAsync("/api/profile/me/banner/upload-url", new
        {
            FileName = "banner.webp",
            ContentType = "image/webp",
            FileSizeBytes = 1024
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("/banner/");
        body.Should().Contain(".webp");
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/gif")]
    public async Task AvatarUploadUrl_InvalidContentType_Returns400(string contentType)
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PostAsJsonAsync("/api/profile/me/avatar/upload-url", new
        {
            FileName = "avatar.svg",
            ContentType = contentType,
            FileSizeBytes = 1024
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AvatarUploadUrl_OversizedAvatar_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PostAsJsonAsync("/api/profile/me/avatar/upload-url", new
        {
            FileName = "avatar.png",
            ContentType = "image/png",
            FileSizeBytes = 5 * 1024 * 1024 + 1
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BannerUploadUrl_OversizedBanner_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PostAsJsonAsync("/api/profile/me/banner/upload-url", new
        {
            FileName = "banner.png",
            ContentType = "image/png",
            FileSizeBytes = 10 * 1024 * 1024 + 1
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ConfirmAvatarUpload_UpdatesAvatar()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);
        var objectKey = await CreateUploadObjectKeyAsync(client, "/api/profile/me/avatar/upload-url", "image/png", "avatar.png");

        var response = await client.PostAsJsonAsync("/api/profile/me/avatar/confirm", new { ObjectKey = objectKey });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"hasUploadedAvatar\":true");
        body.Should().NotContain("email");
        body.Should().NotContain("passwordHash");
    }

    [Fact]
    public async Task ConfirmBannerUpload_UpdatesBanner()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);
        var objectKey = await CreateUploadObjectKeyAsync(client, "/api/profile/me/banner/upload-url", "image/png", "banner.png");

        var response = await client.PostAsJsonAsync("/api/profile/me/banner/confirm", new { ObjectKey = objectKey });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"hasUploadedBanner\":true");
    }

    [Fact]
    public async Task RemoveAvatar_ClearsAvatar()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);
        var objectKey = await CreateUploadObjectKeyAsync(client, "/api/profile/me/avatar/upload-url", "image/png", "avatar.png");
        await client.PostAsJsonAsync("/api/profile/me/avatar/confirm", new { ObjectKey = objectKey });

        var response = await client.DeleteAsync("/api/profile/me/avatar");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"hasUploadedAvatar\":false");
    }

    [Fact]
    public async Task RemoveBanner_ClearsBanner()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);
        var objectKey = await CreateUploadObjectKeyAsync(client, "/api/profile/me/banner/upload-url", "image/png", "banner.png");
        await client.PostAsJsonAsync("/api/profile/me/banner/confirm", new { ObjectKey = objectKey });

        var response = await client.DeleteAsync("/api/profile/me/banner");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"hasUploadedBanner\":false");
    }

    [Fact]
    public async Task UpdateAvatarFallbackColor_ValidColor_ReturnsUpdatedColor()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PutAsJsonAsync("/api/profile/me/avatar/fallback", new { Color = "#3366AA" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("#3366AA");
    }

    [Fact]
    public async Task UpdateMe_EmptyDisplayName_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PutAsJsonAsync("/api/profile/me", new
        {
            DisplayName = "",
            Visibility = "Public"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateMe_InvalidProfileType_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PutAsJsonAsync("/api/profile/me", new
        {
            DisplayName = "Test User",
            Visibility = "Public",
            ProfileType = "Admin"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateMe_CannotModifyEmail_OrAuthFields()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        // Update profile with display name only — email is not in the DTO.
        await client.PutAsJsonAsync("/api/profile/me", new
        {
            DisplayName = "Changed Name",
            Visibility = "Public"
        });

        // Verify /me still shows original email.
        var meResponse = await client.GetAsync("/api/auth/me");
        var meBody = await meResponse.Content.ReadAsStringAsync();
        meBody.Should().Contain(email);
    }

    // ── /api/profile/{username} ──────────────────────────────────────────────

    [Fact]
    public async Task GetPublicProfile_PublicUser_VisibleAnonymously()
    {
        using var registrationClient = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(registrationClient, email, username);

        // Anonymous request
        using var anonClient = CreateClient();
        var response = await anonClient.GetAsync($"/api/profile/{username}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(username);
        body.Should().NotContain("email");
    }

    [Fact]
    public async Task GetPublicProfile_PrivateUser_Returns403ForOthers()
    {
        // Register user and set profile to private.
        using var ownerClient = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(ownerClient, email, username);
        await ownerClient.PutAsJsonAsync("/api/profile/me", new
        {
            DisplayName = "Private User",
            Visibility = "Private"
        });

        // Another user tries to view.
        using var otherClient = CreateClient();
        var response = await otherClient.GetAsync($"/api/profile/{username}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetPublicProfile_FriendsOnlyUser_Returns403ForOthers()
    {
        using var ownerClient = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(ownerClient, email, username);
        await ownerClient.PutAsJsonAsync("/api/profile/me", new
        {
            DisplayName = "Friends Only User",
            Visibility = "FriendsOnly"
        });

        using var otherClient = CreateClient();
        var response = await otherClient.GetAsync($"/api/profile/{username}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetPublicProfile_PrivateUser_OwnerCanView()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);
        await client.PutAsJsonAsync("/api/profile/me", new
        {
            DisplayName = "Private User",
            Visibility = "Private"
        });

        var response = await client.GetAsync($"/api/profile/{username}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetPublicProfile_UnknownUsername_Returns404()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/profile/definitelynotauser99xyz");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Username update ───────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateUsername_NewAvailableHandle_Returns200()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var newHandle = "handle" + Guid.NewGuid().ToString("N")[..6];
        var response = await client.PutAsJsonAsync("/api/profile/me/username", new { Username = newHandle });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateUsername_Taken_Returns409()
    {
        using var client1 = CreateClient();
        var (e1, u1) = UniqueUser();
        await RegisterAndLoginAsync(client1, e1, u1);

        using var client2 = CreateClient();
        var (e2, u2) = UniqueUser();
        await RegisterAndLoginAsync(client2, e2, u2);

        // client2 tries to take u1's username.
        var response = await client2.PutAsJsonAsync("/api/profile/me/username", new { Username = u1 });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── Links ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateLinks_ValidLinks_Returns200()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PutAsJsonAsync("/api/profile/me/links", new
        {
            Links = new[]
            {
                new { Platform = "github", Url = "https://github.com/testuser", DisplayLabel = (string?)null, SortOrder = 0 },
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("github");
    }

    [Fact]
    public async Task UpdateLinks_InvalidPlatform_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PutAsJsonAsync("/api/profile/me/links", new
        {
            Links = new[] { new { Platform = "myspace", Url = "https://myspace.com", DisplayLabel = (string?)null, SortOrder = 0 } }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateLinks_NonHttpsUrl_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PutAsJsonAsync("/api/profile/me/links", new
        {
            Links = new[] { new { Platform = "github", Url = "http://github.com/testuser", DisplayLabel = (string?)null, SortOrder = 0 } }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetPublicProfile_PublicUser_IncludesLinksAndProfileType()
    {
        using var ownerClient = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(ownerClient, email, username);
        await ownerClient.PutAsJsonAsync("/api/profile/me", new
        {
            DisplayName = "Developer User",
            Visibility = "Public",
            ProfileType = "Developer"
        });
        await ownerClient.PutAsJsonAsync("/api/profile/me/links", new
        {
            Links = new[]
            {
                new { Platform = "github", Url = "https://github.com/testuser", DisplayLabel = "Code", SortOrder = 0 },
                new { Platform = "twitter", Url = "https://x.com/testuser", DisplayLabel = "X", SortOrder = 1 },
            }
        });

        using var anonClient = CreateClient();
        var response = await anonClient.GetAsync($"/api/profile/{username}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"profileType\":\"Developer\"");
        body.Should().Contain("github");
        body.Should().Contain("xtwitter");
        body.Should().Contain("https://github.com/testuser");
    }

    // ── Interests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateInterests_ValidTags_Returns200()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PutAsJsonAsync("/api/profile/me/interests",
            new { Interests = new[] { "board-games", "puzzle-games" } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("board-games");
    }

    [Fact]
    public async Task UpdateInterests_InvalidTag_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PutAsJsonAsync("/api/profile/me/interests",
            new { Interests = new[] { "underwater-basket-weaving" } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── FriendsOnly & block visibility ───────────────────────────────────────

    [Fact]
    public async Task GetPublicProfile_FriendsOnlyUser_FriendCanView_Returns200()
    {
        using var ownerClient = CreateClient();
        var (ownerEmail, ownerUsername) = UniqueUser();
        await RegisterAndLoginAsync(ownerClient, ownerEmail, ownerUsername);

        using var friendClient = CreateClient();
        var (friendEmail, friendUsername) = UniqueUser();
        await RegisterAndLoginAsync(friendClient, friendEmail, friendUsername);

        // Owner sets visibility to FriendsOnly
        await ownerClient.PutAsJsonAsync("/api/profile/me", new
        {
            DisplayName = "FriendsOnly Owner",
            Visibility = "FriendsOnly"
        });

        // Friend sends a request; owner accepts
        var ownerProfile = await friendClient.GetAsync($"/api/profile/{ownerUsername}");
        // FriendsOnly is visible in suggestions but the profile page denies strangers — confirm 403 first
        ownerProfile.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Get owner userId from ownerClient (they can always see their own profile)
        var ownerSelf = await ownerClient.GetAsync("/api/profile/me");
        var ownerBody = await ownerSelf.Content.ReadAsStringAsync();
        var ownerJson = System.Text.Json.JsonDocument.Parse(ownerBody);
        var ownerUserId = ownerJson.RootElement.GetProperty("userId").GetGuid();

        // Friend sends request
        await friendClient.PostAsJsonAsync("/api/friends/requests", new { TargetUserId = ownerUserId });

        // Owner finds the incoming request and accepts
        var requestsResp = await ownerClient.GetAsync("/api/friends/requests?direction=incoming");
        var requestsBody = await requestsResp.Content.ReadAsStringAsync();
        var requestsJson = System.Text.Json.JsonDocument.Parse(requestsBody);
        var requestId = requestsJson.RootElement.GetProperty("items")[0].GetProperty("requestId").GetGuid();
        await ownerClient.PostAsJsonAsync($"/api/friends/requests/{requestId}/accept", (object?)null);

        // Friend can now view the FriendsOnly profile
        var response = await friendClient.GetAsync($"/api/profile/{ownerUsername}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(ownerUsername);
    }

    [Fact]
    public async Task GetPublicProfile_BlockedUser_Returns403()
    {
        using var ownerClient = CreateClient();
        var (ownerEmail, ownerUsername) = UniqueUser();
        await RegisterAndLoginAsync(ownerClient, ownerEmail, ownerUsername);

        using var otherClient = CreateClient();
        var (otherEmail, otherUsername) = UniqueUser();
        await RegisterAndLoginAsync(otherClient, otherEmail, otherUsername);

        // Owner gets other's userId and blocks them
        var otherProfile = await ownerClient.GetAsync($"/api/profile/{otherUsername}");
        var otherJson = System.Text.Json.JsonDocument.Parse(await otherProfile.Content.ReadAsStringAsync());
        var otherUserId = otherJson.RootElement.GetProperty("userId").GetGuid();
        await ownerClient.PostAsJsonAsync("/api/friends/blocks", new { TargetUserId = otherUserId });

        // The blocked party tries to view the owner's public profile
        var response = await otherClient.GetAsync($"/api/profile/{ownerUsername}");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Profile.Blocked");
    }

    // ── Security regression tests ──────────────────────────────────────────────

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/gif")]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    public async Task UploadUrl_RejectedContentTypes_Return400(string contentType)
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PostAsJsonAsync("/api/profile/me/avatar/upload-url", new
        {
            FileName = "file",
            ContentType = contentType,
            FileSizeBytes = 1024
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateMe_ArbitraryAvatarUrlInBody_IsIgnored()
    {
        // Even if a client sends AvatarUrl in the JSON body, it must not affect the stored avatar.
        // The field is removed from UpdateProfileRequestDto; ASP.NET ignores unknown JSON keys.
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PutAsJsonAsync("/api/profile/me", new
        {
            DisplayName = "Test",
            AvatarUrl = "https://tracking.evil.example/pixel.gif",
            BannerUrl = "https://tracking.evil.example/banner.gif",
            Visibility = "Public"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("tracking.evil.example");
        body.Should().Contain("\"hasUploadedAvatar\":false");
    }

    [Fact]
    public async Task UpdateMe_DeveloperProfileType_DoesNotElevateRole()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        await client.PutAsJsonAsync("/api/profile/me", new
        {
            DisplayName = "Dev User",
            ProfileType = "Developer",
            Visibility = "Public"
        });

        var profile = await client.GetAsync("/api/profile/me");
        var body = await profile.Content.ReadAsStringAsync();
        body.Should().Contain("\"profileType\":\"Developer\"");
        body.Should().Contain("\"role\":\"Player\"");
        body.Should().NotContain("\"role\":\"Admin\"");
    }

    [Fact]
    public async Task GetMe_RegionIsNotEuWest()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.GetAsync("/api/profile/me");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain("eu-west");
    }

    [Fact]
    public async Task GetPublicProfile_PrivateProfile_DoesNotExposeEmailOrAuthFields()
    {
        using var ownerClient = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(ownerClient, email, username);
        await ownerClient.PutAsJsonAsync("/api/profile/me", new
        {
            DisplayName = "Private User",
            Visibility = "Public"
        });

        using var anonClient = CreateClient();
        var response = await anonClient.GetAsync($"/api/profile/{username}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("passwordHash");
        body.Should().NotContain("failedLoginCount");
        body.Should().NotContain("isSuspended");
        body.Should().NotContain("googleId");
        body.Should().NotContain("email");
    }

    [Fact]
    public async Task UpdateLinks_WebsitePlatform_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PutAsJsonAsync("/api/profile/me/links", new
        {
            Links = new[] { new { Platform = "website", Url = "https://example.com", DisplayLabel = (string?)null, SortOrder = 0 } }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateLinks_JavascriptUrl_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PutAsJsonAsync("/api/profile/me/links", new
        {
            Links = new[] { new { Platform = "github", Url = "javascript:alert(1)", DisplayLabel = (string?)null, SortOrder = 0 } }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateAvatarFallbackColor_InvalidColor_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PutAsJsonAsync("/api/profile/me/avatar/fallback",
            new { Color = "red; injection" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateBannerFallbackColor_InvalidColor_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PutAsJsonAsync("/api/profile/me/banner/fallback",
            new { Color = "notahexcolor" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ConfirmAvatarUpload_AnotherUsersObjectKey_Returns400()
    {
        using var ownerClient = CreateClient();
        var (e1, u1) = UniqueUser();
        await RegisterAndLoginAsync(ownerClient, e1, u1);
        var ownerKey = await CreateUploadObjectKeyAsync(ownerClient, "/api/profile/me/avatar/upload-url", "image/png", "avatar.png");

        // Second user tries to confirm the first user's object key.
        using var attackerClient = CreateClient();
        var (e2, u2) = UniqueUser();
        await RegisterAndLoginAsync(attackerClient, e2, u2);

        var response = await attackerClient.PostAsJsonAsync("/api/profile/me/avatar/confirm",
            new { ObjectKey = ownerKey });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (string email, string username) UniqueUser()
    {
        var s = Guid.NewGuid().ToString("N")[..8];
        return ($"prof-{s}@example.com", $"prof{s}");
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
            Password = "ValidPassword1",
            ConfirmPassword = "ValidPassword1",
            CaptchaToken = "test-captcha-token"
        });
        await client.PostAsJsonAsync("/api/auth/login", new
        {
            EmailOrUsername = email,
            Password = "ValidPassword1",
            CaptchaToken = "test-captcha-token"
        });
    }

    private static async Task<string> CreateUploadObjectKeyAsync(
        HttpClient client,
        string path,
        string contentType,
        string fileName)
    {
        var response = await client.PostAsJsonAsync(path, new
        {
            FileName = fileName,
            ContentType = contentType,
            FileSizeBytes = 1024
        });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<UploadUrlResponse>();
        return json!.ObjectKey;
    }

    private sealed record UploadUrlResponse(string ObjectKey);

    public void Dispose() => _factory.Dispose();
}
