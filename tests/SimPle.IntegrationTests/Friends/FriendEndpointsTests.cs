using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using SimPle.IntegrationTests.Auth;

namespace SimPle.IntegrationTests.Friends;

public sealed class FriendEndpointsTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    // ── Auth guards ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSummary_Unauthenticated_Returns401()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/friends/summary");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SendFriendRequest_Unauthenticated_Returns401()
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/friends/requests", new { TargetUserId = Guid.NewGuid() });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BlockUser_Unauthenticated_Returns401()
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/friends/blocks", new { TargetUserId = Guid.NewGuid() });
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetFriends_Unauthenticated_Returns401()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/friends");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetSettings_Unauthenticated_Returns401()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/api/friends/settings");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── CSRF guards ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SendFriendRequest_MissingCsrfHeader_Returns400()
    {
        // Log in with a CSRF-capable client so the auth cookie is set, then remove the CSRF
        // header before making the mutation request to test the guard in isolation.
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);
        client.DefaultRequestHeaders.Remove("X-Requested-With");

        var response = await client.PostAsJsonAsync("/api/friends/requests", new { TargetUserId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Auth.CsrfHeaderRequired");
    }

    [Fact]
    public async Task UpdateSettings_MissingCsrfHeader_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);
        client.DefaultRequestHeaders.Remove("X-Requested-With");

        var response = await client.PutAsJsonAsync("/api/friends/settings",
            new { FriendRequestPrivacy = "Anyone" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Auth.CsrfHeaderRequired");
    }

    // ── Pagination validation ────────────────────────────────────────────────

    [Fact]
    public async Task GetFriends_InvalidCursor_Returns400InvalidCursor()
    {
        // Reconciliation R3: paging is keyset (?limit=&cursor=). A malformed opaque cursor → 400 Pagination.InvalidCursor.
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.GetAsync("/api/friends?cursor=@@not-a-cursor@@");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Pagination.InvalidCursor");
    }

    [Fact]
    public async Task GetFriends_LegacyOffsetParams_AreIgnored_Returns200()
    {
        // Reconciliation R3: the retired ?page=/?pageSize= offset params are simply ignored (no 400).
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.GetAsync("/api/friends?page=0&pageSize=200");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetFriends_LimitOutOfRange_IsClamped_Returns200()
    {
        // Reconciliation R3: limit is clamped server-side, never rejected.
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.GetAsync("/api/friends?limit=500");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetRequests_InvalidDirection_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.GetAsync("/api/friends/requests?direction=sideways");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Validation.Failed");
    }

    [Fact]
    public async Task GetSuggestions_LimitOutOfRange_IsClamped_Returns200()
    {
        // Reconciliation R3: suggestions limit is clamped to 1..20 server-side, never rejected.
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.GetAsync("/api/friends/suggestions?limit=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Search validation ────────────────────────────────────────────────────

    [Fact]
    public async Task GetFriends_QueryOver100Chars_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var longQuery = new string('a', 101);
        var response = await client.GetAsync($"/api/friends?query={longQuery}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        // Reconciliation R12: canonical catalogue collapses the old Validation.QueryTooLong into Validation.Failed.
        body.Should().Contain("Validation.Failed");
    }

    [Fact]
    public async Task GetFriends_LeadingAtSignStripped_Succeeds()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        // '@' prefix is stripped and treated as a valid search — returns 200 with empty list, not 400
        var response = await client.GetAsync("/api/friends?query=%40someuser");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Summary ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSummary_AfterRegister_ReturnsZeroCounts()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.GetAsync("/api/friends/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"friendCount\":0");
        body.Should().Contain("\"incomingRequestCount\":0");
        body.Should().Contain("\"outgoingRequestCount\":0");
    }

    [Fact]
    public async Task GetSummary_AfterSendRequest_OutgoingCountIsOne()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);
        var userBId = await GetUserIdAsync(clientA, usernameB);

        await clientA.PostAsJsonAsync("/api/friends/requests", new { TargetUserId = userBId });

        var response = await clientA.GetAsync("/api/friends/summary");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"outgoingRequestCount\":1");
        body.Should().Contain("\"incomingRequestCount\":0");
    }

    [Fact]
    public async Task GetSummary_AfterAcceptRequest_FriendCountIsOne()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        var requestId = await SendFriendRequestAsync(clientA, clientB, usernameB);
        await clientB.PostAsJsonAsync($"/api/friends/requests/{requestId}/accept", (object?)null);

        var response = await clientA.GetAsync("/api/friends/summary");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"friendCount\":1");
        body.Should().Contain("\"outgoingRequestCount\":0");
    }

    // ── Send friend request ───────────────────────────────────────────────────

    [Fact]
    public async Task SendFriendRequest_ToSelf_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);
        var selfId = await GetMyUserIdAsync(client);

        var response = await client.PostAsJsonAsync("/api/friends/requests", new { TargetUserId = selfId });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Friends.SelfRequest");
    }

    [Fact]
    public async Task SendFriendRequest_ToNonExistentUser_Returns404()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PostAsJsonAsync("/api/friends/requests",
            new { TargetUserId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SendFriendRequest_ValidRequest_Returns201()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);
        var userBId = await GetUserIdAsync(clientA, usernameB);

        var response = await clientA.PostAsJsonAsync("/api/friends/requests",
            new { TargetUserId = userBId });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"status\":\"Pending\"");
    }

    [Fact]
    public async Task SendFriendRequest_SameDirectionDuplicate_Returns200AlreadyPending()
    {
        // Reconciliation R1: a same-direction retry is idempotent (200 already_pending), never 409.
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);
        var userBId = await GetUserIdAsync(clientA, usernameB);

        await clientA.PostAsJsonAsync("/api/friends/requests", new { TargetUserId = userBId });
        var response = await clientA.PostAsJsonAsync("/api/friends/requests", new { TargetUserId = userBId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("already_pending");
    }

    [Fact]
    public async Task SendFriendRequest_ReversePending_Returns200CrossRequestAccepted()
    {
        // Reconciliation R2: a reverse send atomically accepts the existing edge (200 cross_request_accepted).
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        var userAId = await GetUserIdAsync(clientB, usernameA);
        var userBId = await GetUserIdAsync(clientA, usernameB);

        // B sends to A first
        await clientB.PostAsJsonAsync("/api/friends/requests", new { TargetUserId = userAId });

        // A sends to B — B's pending request exists → atomic cross-accept
        var response = await clientA.PostAsJsonAsync("/api/friends/requests", new { TargetUserId = userBId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("cross_request_accepted");
        body.Should().Contain("\"status\":\"Accepted\"");

        // Both parties now see exactly one friend.
        var summaryA = await clientA.GetAsync("/api/friends/summary");
        (await summaryA.Content.ReadAsStringAsync()).Should().Contain("\"friendCount\":1");
        var summaryB = await clientB.GetAsync("/api/friends/summary");
        (await summaryB.Content.ReadAsStringAsync()).Should().Contain("\"friendCount\":1");
    }

    [Fact]
    public async Task SendFriendRequest_ToAlreadyFriend_Returns409AlreadyFriends()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        var requestId = await SendFriendRequestAsync(clientA, clientB, usernameB);
        await clientB.PostAsJsonAsync($"/api/friends/requests/{requestId}/accept", (object?)null);

        var userBId = await GetUserIdAsync(clientA, usernameB);
        var response = await clientA.PostAsJsonAsync("/api/friends/requests", new { TargetUserId = userBId });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Friends.AlreadyFriends");
    }

    // ── Privacy: Off ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SendFriendRequest_TargetHasPrivacyOff_Returns400()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        // B sets privacy to Off
        await clientB.PutAsJsonAsync("/api/friends/settings", new { FriendRequestPrivacy = "Off" });

        var userBId = await GetUserIdAsync(clientA, usernameB);
        var response = await clientA.PostAsJsonAsync("/api/friends/requests", new { TargetUserId = userBId });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Friends.RequestsDisabled");
    }

    // ── Accept / Decline / Cancel ─────────────────────────────────────────────

    [Fact]
    public async Task AcceptFriendRequest_ByAddressee_Returns200()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        var requestId = await SendFriendRequestAsync(clientA, clientB, usernameB);
        var response = await clientB.PostAsJsonAsync($"/api/friends/requests/{requestId}/accept", (object?)null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"status\":\"Accepted\"");
    }

    [Fact]
    public async Task AcceptFriendRequest_ByRequester_Returns404NotVisible()
    {
        // Reconciliation R8: BOLA is privacy-safe — a non-addressee acting on the id gets 404 Profile.NotVisible, never 403.
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        var requestId = await SendFriendRequestAsync(clientA, clientB, usernameB);

        // A tries to accept their own outgoing request — only addressee may accept
        var response = await clientA.PostAsJsonAsync($"/api/friends/requests/{requestId}/accept", (object?)null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Profile.NotVisible");
    }

    [Fact]
    public async Task AcceptFriendRequest_AlreadyAccepted_IsIdempotent_Returns200()
    {
        // Reconciliation R1: a repeat accept while already Accepted is idempotent (200 + Accepted), not 400.
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        var requestId = await SendFriendRequestAsync(clientA, clientB, usernameB);
        await clientB.PostAsJsonAsync($"/api/friends/requests/{requestId}/accept", (object?)null);

        // Accepting again — already Accepted, returns the same edge idempotently.
        var response = await clientB.PostAsJsonAsync($"/api/friends/requests/{requestId}/accept", (object?)null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"status\":\"Accepted\"");
    }

    [Fact]
    public async Task DeclineFriendRequest_ByAddressee_Returns200()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        var requestId = await SendFriendRequestAsync(clientA, clientB, usernameB);
        var response = await clientB.PostAsJsonAsync($"/api/friends/requests/{requestId}/decline", (object?)null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"status\":\"Declined\"");
    }

    [Fact]
    public async Task DeclineFriendRequest_ByRequester_Returns404NotVisible()
    {
        // Reconciliation R8: only the addressee may decline; the requester gets a privacy-safe 404, never 403.
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        var requestId = await SendFriendRequestAsync(clientA, clientB, usernameB);
        var response = await clientA.PostAsJsonAsync($"/api/friends/requests/{requestId}/decline", (object?)null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Profile.NotVisible");
    }

    [Fact]
    public async Task CancelFriendRequest_ByRequester_Returns204()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        var requestId = await SendFriendRequestAsync(clientA, clientB, usernameB);
        var response = await clientA.DeleteAsync($"/api/friends/requests/{requestId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CancelFriendRequest_ByAddressee_Returns404NotVisible()
    {
        // Reconciliation R8: only the requester may cancel; the addressee gets a privacy-safe 404, never 403.
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        var requestId = await SendFriendRequestAsync(clientA, clientB, usernameB);

        // B (addressee) tries to cancel A's outgoing request
        var response = await clientB.DeleteAsync($"/api/friends/requests/{requestId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Profile.NotVisible");
    }

    [Fact]
    public async Task CancelFriendRequest_NonExistent_Returns404()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.DeleteAsync($"/api/friends/requests/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Remove friend ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveFriend_ByRequester_Returns204()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        var requestId = await SendFriendRequestAsync(clientA, clientB, usernameB);
        await clientB.PostAsJsonAsync($"/api/friends/requests/{requestId}/accept", (object?)null);

        var userBId = await GetUserIdAsync(clientA, usernameB);
        var response = await clientA.DeleteAsync($"/api/friends/{userBId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task RemoveFriend_ByAddressee_Returns204()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        var requestId = await SendFriendRequestAsync(clientA, clientB, usernameB);
        await clientB.PostAsJsonAsync($"/api/friends/requests/{requestId}/accept", (object?)null);

        var userAId = await GetUserIdAsync(clientB, usernameA);
        var response = await clientB.DeleteAsync($"/api/friends/{userAId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task RemoveFriend_NotFriends_Returns404()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);
        var userBId = await GetUserIdAsync(clientA, usernameB);

        var response = await clientA.DeleteAsync($"/api/friends/{userBId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Blocks ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BlockUser_SelfBlock_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);
        var selfId = await GetMyUserIdAsync(client);

        var response = await client.PostAsJsonAsync("/api/friends/blocks", new { TargetUserId = selfId });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Friends.SelfBlock");
    }

    [Fact]
    public async Task BlockUser_ValidTarget_Returns201()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);
        var userBId = await GetUserIdAsync(clientA, usernameB);

        var response = await clientA.PostAsJsonAsync("/api/friends/blocks", new { TargetUserId = userBId });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"blockedUserId\"");
    }

    [Fact]
    public async Task BlockUser_PrivateTarget_ResponseDoesNotExposeIdentityCard()
    {
        // M03-007 fix: unlike Discover/SendFriendRequest, blocking never gated on the target's visibility —
        // it must not echo the target's identity (username/displayName/avatar) even when they are Private.
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);
        var userBId = await GetUserIdAsync(clientA, usernameB);

        await clientB.PutAsJsonAsync("/api/profile/me", new { DisplayName = "Private Target", Visibility = "Private" });

        var response = await clientA.PostAsJsonAsync("/api/friends/blocks", new { TargetUserId = userBId });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"blockedUserId\"");
        body.Should().NotContain(usernameB);
        body.Should().NotContain("Private Target");
        body.Should().NotContain("blockedUsername");
        body.Should().NotContain("blockedDisplayName");
        body.Should().NotContain("blockedAvatarUrl");
    }

    [Fact]
    public async Task BlockUser_AlreadyBlocked_IsIdempotent_Returns200()
    {
        // Reconciliation: a repeat block is idempotent (200 already_blocked outcome), not 409.
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);
        var userBId = await GetUserIdAsync(clientA, usernameB);

        await clientA.PostAsJsonAsync("/api/friends/blocks", new { TargetUserId = userBId });
        var response = await clientA.PostAsJsonAsync("/api/friends/blocks", new { TargetUserId = userBId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("already_blocked");
    }

    [Fact]
    public async Task BlockUser_CancelsActiveFriendship()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        // Establish friendship
        var requestId = await SendFriendRequestAsync(clientA, clientB, usernameB);
        await clientB.PostAsJsonAsync($"/api/friends/requests/{requestId}/accept", (object?)null);

        // Verify they are friends
        var summaryBefore = await clientA.GetAsync("/api/friends/summary");
        var bodyBefore = await summaryBefore.Content.ReadAsStringAsync();
        bodyBefore.Should().Contain("\"friendCount\":1");

        // A blocks B — should cancel friendship
        var userBId = await GetUserIdAsync(clientA, usernameB);
        var blockResponse = await clientA.PostAsJsonAsync("/api/friends/blocks", new { TargetUserId = userBId });
        blockResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        // A's friend count drops to 0
        var summaryAfter = await clientA.GetAsync("/api/friends/summary");
        var bodyAfter = await summaryAfter.Content.ReadAsStringAsync();
        bodyAfter.Should().Contain("\"friendCount\":0");
    }

    [Fact]
    public async Task BlockUser_BlockedPartyCannotSendRequest_Returns404NotVisible()
    {
        // Reconciliation R8: a block makes the blocker invisible — the blocked party's send is 404 Profile.NotVisible,
        // not a distinguishable Friends.Blocked, so the block cannot be probed.
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        // Fetch both user IDs before any blocking so the GET profile succeeds for both parties.
        var userAId = await GetUserIdAsync(clientB, usernameA);
        var userBId = await GetUserIdAsync(clientA, usernameB);

        await clientA.PostAsJsonAsync("/api/friends/blocks", new { TargetUserId = userBId });

        // B (who was blocked) tries to send a request to A — should be rejected as not-visible
        var response = await clientB.PostAsJsonAsync("/api/friends/requests", new { TargetUserId = userAId });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Profile.NotVisible");
    }

    [Fact]
    public async Task UnblockUser_ValidBlock_Returns204()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);
        var userBId = await GetUserIdAsync(clientA, usernameB);

        await clientA.PostAsJsonAsync("/api/friends/blocks", new { TargetUserId = userBId });
        var response = await clientA.DeleteAsync($"/api/friends/blocks/{userBId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task UnblockUser_NotBlocked_IsIdempotent_Returns204()
    {
        // Reconciliation: unblocking a non-existent block is idempotent (restores nothing) → 204, not 404.
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.DeleteAsync($"/api/friends/blocks/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ── Settings ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSettings_NoRowExists_ReturnsDefaultAnyone()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.GetAsync("/api/friends/settings");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"friendRequestPrivacy\":\"Anyone\"");
    }

    [Fact]
    public async Task GetSettings_CalledTwice_StillReturnsDefaultAnyone()
    {
        // Verifies GET settings is read-only (does not insert a row)
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        await client.GetAsync("/api/friends/settings");
        var response2 = await client.GetAsync("/api/friends/settings");

        response2.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response2.Content.ReadAsStringAsync();
        body.Should().Contain("\"friendRequestPrivacy\":\"Anyone\"");
    }

    [Fact]
    public async Task UpdateSettings_ValidPrivacy_Returns200()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PutAsJsonAsync("/api/friends/settings",
            new { FriendRequestPrivacy = "FriendsOfFriends" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"friendRequestPrivacy\":\"FriendsOfFriends\"");
    }

    [Fact]
    public async Task UpdateSettings_InvalidPrivacy_Returns400()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PutAsJsonAsync("/api/friends/settings",
            new { FriendRequestPrivacy = "NotAValidValue" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateSettings_ToOff_ThenSenderGets400()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        // B sets privacy to Off
        await clientB.PutAsJsonAsync("/api/friends/settings", new { FriendRequestPrivacy = "Off" });

        var userBId = await GetUserIdAsync(clientA, usernameB);
        var response = await clientA.PostAsJsonAsync("/api/friends/requests", new { TargetUserId = userBId });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Friends.RequestsDisabled");
    }

    // ── Profile friend count ──────────────────────────────────────────────────

    [Fact]
    public async Task GetPublicProfile_AfterFriendship_ShowsCorrectFriendCount()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        // Establish A–B friendship
        var requestId = await SendFriendRequestAsync(clientA, clientB, usernameB);
        await clientB.PostAsJsonAsync($"/api/friends/requests/{requestId}/accept", (object?)null);

        // View A's public profile — should show friendCount = 1
        var response = await clientA.GetAsync($"/api/profile/{usernameA}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"friendCount\":1");
    }

    // ── Suggestions ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSuggestions_ExcludesAcceptedFriends()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        var requestId = await SendFriendRequestAsync(clientA, clientB, usernameB);
        await clientB.PostAsJsonAsync($"/api/friends/requests/{requestId}/accept", (object?)null);

        var response = await clientA.GetAsync("/api/friends/suggestions?limit=50");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(usernameB);
    }

    [Fact]
    public async Task GetSuggestions_ExcludesBlockedUsers()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        var userBId = await GetUserIdAsync(clientA, usernameB);
        await clientA.PostAsJsonAsync("/api/friends/blocks", new { TargetUserId = userBId });

        var response = await clientA.GetAsync("/api/friends/suggestions?limit=50");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(usernameB);
    }

    [Fact]
    public async Task GetSuggestions_ReturnsValidResponse()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.GetAsync("/api/friends/suggestions?limit=10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Incoming/outgoing requests listing ────────────────────────────────────

    [Fact]
    public async Task GetRequests_Incoming_ShowsPendingRequestsForAddressee()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        await SendFriendRequestAsync(clientA, clientB, usernameB);

        // B checks incoming
        var response = await clientB.GetAsync("/api/friends/requests?direction=incoming");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(usernameA);
    }

    [Fact]
    public async Task GetRequests_Outgoing_ShowsPendingRequestsForRequester()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        await SendFriendRequestAsync(clientA, clientB, usernameB);

        var response = await clientA.GetAsync("/api/friends/requests?direction=outgoing");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(usernameB);
    }

    // NOTE: the old "declined request can be immediately re-sent → 201" test was removed in Sub-session C.
    // Reconciliation R7 introduced a 7-day decline cooldown, so an immediate resend is now 409
    // Friends.RequestCooldown (see SendFriendRequest_AfterDeclineImmediateResend_Returns409CooldownWithRetryAfter).
    // The post-cooldown resend-succeeds path is time-gated and covered by FriendsServiceTests unit tests.

    // ── Discovery (R4) ────────────────────────────────────────────────────────

    [Fact]
    public async Task Discover_ExactPublicUsername_Returns200MinimalDto()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        var response = await clientA.GetAsync($"/api/friends/discovery?username={usernameB}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(usernameB);
        body.Should().Contain("\"userId\"");
        // R5: discovery DTO must never leak gameplay stats.
        body.ToLowerInvariant().Should().NotContain("\"level\"");
        body.ToLowerInvariant().Should().NotContain("\"elo\"");
    }

    [Fact]
    public async Task Discover_NonexistentUsername_Returns404NotVisible()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.GetAsync("/api/friends/discovery?username=nobodyhere123");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Profile.NotVisible");
    }

    [Fact]
    public async Task Discover_TargetWithPrivacyOff_Returns404NotVisible()
    {
        // R4: timing-safe discovery treats an existing-but-ineligible account identically to a missing one.
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);
        await clientB.PutAsJsonAsync("/api/friends/settings", new { FriendRequestPrivacy = "Off" });

        var response = await clientA.GetAsync($"/api/friends/discovery?username={usernameB}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Profile.NotVisible");
    }

    [Fact]
    public async Task Discover_MalformedUsername_Returns400ValidationFailed()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        // 2 chars fails the ^[a-zA-Z0-9_.-]{3,30}$ handle rule.
        var response = await client.GetAsync("/api/friends/discovery?username=ab");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Validation.Failed");
    }

    // ── Dismiss suggestion (R10) ──────────────────────────────────────────────

    [Fact]
    public async Task DismissSuggestion_ValidTarget_Returns204AndIsIdempotent()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);
        var userBId = await GetUserIdAsync(clientA, usernameB);

        var first = await clientA.PutAsJsonAsync($"/api/friends/suggestions/{userBId}/dismiss", (object?)null);
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Idempotent repeat within the suppression window.
        var second = await clientA.PutAsJsonAsync($"/api/friends/suggestions/{userBId}/dismiss", (object?)null);
        second.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // A dismissed user must not surface in suggestions.
        var suggestions = await clientA.GetAsync("/api/friends/suggestions?limit=50");
        (await suggestions.Content.ReadAsStringAsync()).Should().NotContain(usernameB);
    }

    [Fact]
    public async Task DismissSuggestion_NonexistentTarget_Returns404NotVisible()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PutAsJsonAsync(
            $"/api/friends/suggestions/{Guid.NewGuid()}/dismiss", (object?)null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Profile.NotVisible");
    }

    // ── Cooldown (R7) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SendFriendRequest_AfterDeclineImmediateResend_Returns409CooldownWithRetryAfter()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        // A→B, B declines → A is under a 7-day resend cooldown to B.
        var requestId = await SendFriendRequestAsync(clientA, clientB, usernameB);
        await clientB.PostAsJsonAsync($"/api/friends/requests/{requestId}/decline", (object?)null);

        var userBId = await GetUserIdAsync(clientA, usernameB);
        var response = await clientA.PostAsJsonAsync("/api/friends/requests", new { TargetUserId = userBId });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Headers.Contains("Retry-After").Should().BeTrue();
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Friends.RequestCooldown");
        body.Should().Contain("retryAfterUtc");
    }

    // ── BOLA on guessed id (R8) ───────────────────────────────────────────────

    [Fact]
    public async Task AcceptFriendRequest_GuessedUnrelatedId_Returns404NotVisible()
    {
        using var client = CreateClient();
        var (email, username) = UniqueUser();
        await RegisterAndLoginAsync(client, email, username);

        var response = await client.PostAsJsonAsync(
            $"/api/friends/requests/{Guid.NewGuid()}/accept", (object?)null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Profile.NotVisible");
    }

    // ── DTO leak guard (R5) ───────────────────────────────────────────────────

    [Fact]
    public async Task GetFriends_DtoContainsNoLevelOrElo()
    {
        using var clientA = CreateClient();
        var (emailA, usernameA) = UniqueUser();
        await RegisterAndLoginAsync(clientA, emailA, usernameA);

        using var clientB = CreateClient();
        var (emailB, usernameB) = UniqueUser();
        await RegisterAndLoginAsync(clientB, emailB, usernameB);

        var requestId = await SendFriendRequestAsync(clientA, clientB, usernameB);
        await clientB.PostAsJsonAsync($"/api/friends/requests/{requestId}/accept", (object?)null);

        var response = await clientA.GetAsync("/api/friends?limit=50");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(usernameB);   // the friend is present
        body.ToLowerInvariant().Should().NotContain("\"level\"");
        body.ToLowerInvariant().Should().NotContain("\"elo\"");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private const string TestPassword = "ValidPassword1";

    private static (string email, string username) UniqueUser()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return ($"fr-{suffix}@example.com", $"fr{suffix}");
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

    /// Gets the userId of the currently authenticated user by reading /api/profile/me.
    private static async Task<Guid> GetMyUserIdAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/profile/me");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<UserIdHolder>();
        return json!.UserId;
    }

    /// Gets the userId of a user by their username via GET /api/profile/{username}.
    private static async Task<Guid> GetUserIdAsync(HttpClient authenticatedClient, string targetUsername)
    {
        var response = await authenticatedClient.GetAsync($"/api/profile/{targetUsername}");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<UserIdHolder>();
        return json!.UserId;
    }

    /// Sends a friend request from clientA to the user with targetUsername.
    /// Returns the request ID from the reconciled SendFriendRequestResult { outcome, request } envelope.
    private static async Task<Guid> SendFriendRequestAsync(
        HttpClient requesterClient,
        HttpClient addresseeClient,
        string targetUsername)
    {
        // Look up the target's userId using the requester's session (both users registered/logged in)
        var targetId = await GetUserIdAsync(requesterClient, targetUsername);
        var response = await requesterClient.PostAsJsonAsync("/api/friends/requests",
            new { TargetUserId = targetId });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<SendResultHolder>();
        return json!.Request.RequestId;
    }

    private sealed record UserIdHolder(Guid UserId);
    private sealed record RequestIdHolder(Guid RequestId);
    private sealed record SendResultHolder(string Outcome, RequestIdHolder Request);

    public void Dispose() => _factory.Dispose();
}
