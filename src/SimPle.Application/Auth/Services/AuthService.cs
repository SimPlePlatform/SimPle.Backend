using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimPle.Application.Auth.DTOs;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Domain.Users;
using SimPle.Shared.Common;

namespace SimPle.Application.Auth.Services;

public sealed class AuthService : IAuthService
{
    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _tokens;
    private readonly IEmailVerificationTokenRepository _verificationTokens;
    private readonly IPasswordResetTokenRepository _resetTokens;
    private readonly IPasswordHashingService _hasher;
    private readonly ITokenService _tokenService;
    private readonly IEmailService _email;
    private readonly IGoogleTokenValidationService _googleValidator;
    private readonly IRevokedJtiStore _revokedJtis;
    private readonly IRealtimeConnectionCloser _realtimeCloser;
    private readonly AuthOptions _authOptions;
    private readonly EmailOptions _emailOptions;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IUserRepository users,
        IRefreshTokenRepository tokens,
        IEmailVerificationTokenRepository verificationTokens,
        IPasswordResetTokenRepository resetTokens,
        IPasswordHashingService hasher,
        ITokenService tokenService,
        IEmailService email,
        IGoogleTokenValidationService googleValidator,
        IRevokedJtiStore revokedJtis,
        IRealtimeConnectionCloser realtimeCloser,
        IOptions<AuthOptions> authOptions,
        IOptions<EmailOptions> emailOptions,
        ILogger<AuthService> logger)
    {
        _users = users;
        _tokens = tokens;
        _verificationTokens = verificationTokens;
        _resetTokens = resetTokens;
        _hasher = hasher;
        _tokenService = tokenService;
        _email = email;
        _googleValidator = googleValidator;
        _revokedJtis = revokedJtis;
        _realtimeCloser = realtimeCloser;
        _authOptions = authOptions.Value;
        _emailOptions = emailOptions.Value;
        _logger = logger;
    }

    public async Task<bool> EmailExistsAsync(string email, CancellationToken ct = default)
    {
        var normalized = email.Trim().ToUpperInvariant();
        return await _users.ExistsByEmailAsync(normalized, ct);
    }

    public async Task<Result<AuthTokenResult>> GoogleLoginAsync(
        string idToken, string ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var googleInfo = await _googleValidator.ValidateAsync(idToken, ct);
        if (googleInfo is null)
            return Result<AuthTokenResult>.Fail("Auth.InvalidGoogleToken", "The Google credential is invalid or has expired.");

        // Reject tokens where Google has not verified the email — we must not
        // trust ownership of an address that Google itself doesn't vouch for.
        if (!googleInfo.EmailVerified)
            return Result<AuthTokenResult>.Fail("Auth.InvalidGoogleToken", "The Google credential is invalid or has expired.");

        // 1. Try to find an existing user already linked to this Google account.
        var user = await _users.GetByGoogleIdAsync(googleInfo.GoogleId, ct);

        if (user is null)
        {
            // 2. Fall back to matching by email — link the Google ID to an existing account.
            var normalizedEmail = googleInfo.Email.Trim().ToUpperInvariant();
            user = await _users.GetByNormalizedEmailAsync(normalizedEmail, ct);

            if (user is not null)
            {
                user.ConnectGoogle(googleInfo.GoogleId);
                await _users.UpdateAsync(user, ct);
            }
        }

        if (user is null)
        {
            // 3. Brand-new user — provision an account from the Google profile.
            var username = await GenerateUniqueUsernameAsync(googleInfo.GivenName ?? googleInfo.Email.Split('@')[0], ct);
            user = User.CreateWithGoogle(
                username,
                googleInfo.Email,
                googleInfo.DisplayName,
                googleInfo.GoogleId,
                googleInfo.PictureUrl);
            await _users.AddAsync(user, ct);

            try { await _email.SendWelcomeEmailAsync(user.Email, user.DisplayName, CancellationToken.None); }
            catch { /* non-fatal */ }
        }

        if (user.IsAccountSuspended())
            return Result<AuthTokenResult>.Fail("Auth.Suspended", "This account has been suspended.");

        user.ResetFailedLogin();
        await _users.UpdateAsync(user, ct);

        _logger.LogInformation("Security: Google login success. UserId={UserId} Ip={Ip}", user.Id, ipAddress);
        var result = await IssueTokenPairAsync(user, ipAddress, userAgent, ct);
        return Result<AuthTokenResult>.Ok(result);
    }

    public async Task<Result<AuthTokenResult>> RegisterAsync(
        RegisterRequestDto request, string ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var normalizedEmail = request.Email.Trim().ToUpperInvariant();
        var normalizedUsername = request.Username.Trim().ToUpperInvariant();

        if (await _users.ExistsByEmailAsync(normalizedEmail, ct))
            return Result<AuthTokenResult>.Fail("Auth.EmailTaken", "An account with this email already exists.");

        if (await _users.ExistsByUsernameAsync(normalizedUsername, ct))
            return Result<AuthTokenResult>.Fail("Auth.UsernameTaken", "This username is already taken.");

        var passwordHash = _hasher.Hash(request.Password);
        var user = User.Create(request.Username.Trim(), request.Email.Trim(), passwordHash, request.Username.Trim());
        await _users.AddAsync(user, ct);

        // Use an independent token so the HTTP request lifecycle cannot cancel the SMTP
        // connection (the first TLS handshake in a new process can be slow on Windows).
        // SmtpEmailService logs and re-throws; we catch here so registration still succeeds.
        try { await SendVerificationEmailCoreAsync(user, CancellationToken.None); }
        catch { /* logged by SmtpEmailService */ }

        _logger.LogInformation("Security: Register success. UserId={UserId} Username={Username} Ip={Ip}",
            user.Id, user.Username, ipAddress);
        var result = await IssueTokenPairAsync(user, ipAddress, userAgent, ct);
        return Result<AuthTokenResult>.Ok(result);
    }

    public async Task<Result<AuthTokenResult>> LoginAsync(
        LoginRequestDto request, string ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var normalized = request.EmailOrUsername.Trim().ToUpperInvariant();
        var user = await _users.GetByNormalizedEmailOrUsernameAsync(normalized, ct);

        const string InvalidCredentials = "Invalid email/username or password.";

        if (user == null)
        {
            _hasher.Verify(request.Password, "$argon2id$v=19$m=65536,t=3,p=1$AAAAAAAAAAAAAAAA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
            _logger.LogWarning("Security: Login failed — user not found. Ip={Ip}", ipAddress);
            return Result<AuthTokenResult>.Fail("Auth.InvalidCredentials", InvalidCredentials);
        }

        if (user.IsLockedOut())
        {
            _logger.LogWarning("Security: Login blocked — account locked. UserId={UserId} Ip={Ip}", user.Id, ipAddress);
            return Result<AuthTokenResult>.Fail("Auth.LockedOut", InvalidCredentials);
        }

        if (user.IsAccountSuspended())
        {
            _logger.LogWarning("Security: Login blocked — account suspended. UserId={UserId} Ip={Ip}", user.Id, ipAddress);
            return Result<AuthTokenResult>.Fail("Auth.Suspended", "This account has been suspended.");
        }

        if (!_hasher.Verify(request.Password, user.PasswordHash))
        {
            user.RecordFailedLogin(
                _authOptions.MaxFailedLoginAttempts,
                _authOptions.LockoutDurationMinutes);
            await _users.UpdateAsync(user, ct);
            if (user.IsLockedOut())
                _logger.LogWarning("Security: Account locked after failed attempts. UserId={UserId} Ip={Ip}", user.Id, ipAddress);
            else
                _logger.LogWarning("Security: Login failed — wrong password. UserId={UserId} Attempts={Attempts} Ip={Ip}",
                    user.Id, user.FailedLoginCount, ipAddress);
            return Result<AuthTokenResult>.Fail("Auth.InvalidCredentials", InvalidCredentials);
        }

        if (_hasher.NeedsRehash(user.PasswordHash))
        {
            user.UpdatePassword(_hasher.Hash(request.Password));
        }

        user.ResetFailedLogin();
        await _users.UpdateAsync(user, ct);

        _logger.LogInformation("Security: Login success. UserId={UserId} Ip={Ip}", user.Id, ipAddress);
        var result = await IssueTokenPairAsync(user, ipAddress, userAgent, ct);
        return Result<AuthTokenResult>.Ok(result);
    }

    public async Task<Result<AuthTokenResult>> RefreshAsync(
        string rawRefreshToken, string ipAddress, CancellationToken ct = default)
    {
        var tokenHash = _tokenService.HashToken(rawRefreshToken);
        var stored = await _tokens.GetByHashAsync(tokenHash, ct);

        if (stored == null || stored.ExpiresAt < DateTime.UtcNow)
            return Result<AuthTokenResult>.Fail("Auth.InvalidToken", "Refresh token is invalid or has expired.");

        if (stored.IsRevoked)
        {
            _logger.LogWarning("Security: Refresh token replay detected — revoking family. UserId={UserId} FamilyId={FamilyId} Ip={Ip}",
                stored.UserId, stored.FamilyId, ipAddress);
            await _tokens.RevokeAllByFamilyIdAsync(stored.FamilyId, "Reuse detected", ct);
            return Result<AuthTokenResult>.Fail("Auth.TokenReuse", "Session invalidated. Please log in again.");
        }

        var user = await _users.GetByIdAsync(stored.UserId, ct);
        if (user == null)
            return Result<AuthTokenResult>.Fail("Auth.InvalidToken", "Refresh token is invalid.");

        stored.Revoke(ipAddress, "Rotated");
        // TryUpdateAsync returns false if a concurrent request already rotated this token.
        // Treating this as invalid prevents two simultaneous refresh requests from both succeeding.
        var rotated = await _tokens.TryUpdateAsync(stored, ct);
        if (!rotated)
        {
            _logger.LogWarning("Security: Concurrent refresh conflict — token already rotated. UserId={UserId} Ip={Ip}",
                stored.UserId, ipAddress);
            return Result<AuthTokenResult>.Fail("Auth.InvalidToken", "Refresh token is invalid or has expired.");
        }

        var result = await IssueTokenPairAsync(user, ipAddress, null, ct, stored.FamilyId);
        return Result<AuthTokenResult>.Ok(result);
    }

    public async Task<Result> LogoutAsync(string rawRefreshToken, CancellationToken ct = default)
    {
        var tokenHash = _tokenService.HashToken(rawRefreshToken);
        var stored = await _tokens.GetByHashAsync(tokenHash, ct);

        if (stored is { IsRevoked: false })
        {
            // Module 7: also revoke the session family, matching LogoutAllAsync/RevokeSessionAsync. Previously
            // this method revoked only the refresh-token row, leaving a stale-but-still-valid access cookie live
            // until natural expiry — harmless on plain REST, but a realtime hub connection would otherwise survive
            // a "logout" indefinitely and simply reconnect with that cookie. This is an approved, intentional
            // Module 1 behavior change (see docs/specs/module-07-realtime-presence-chat-spec.md).
            _revokedJtis.Revoke(stored.FamilyId.ToString(), TimeSpan.FromMinutes(20));

            stored.Revoke("", "Logout");
            await _tokens.UpdateAsync(stored, ct);

            // Realtime connections cache the authenticated principal for their lifetime and are not
            // automatically revalidated — close them proactively rather than letting a live socket outlive logout.
            await _realtimeCloser.CloseUserConnectionsAsync(stored.UserId, "auth.session_revoked", ct);
        }

        return Result.Ok();
    }

    public async Task<Result> LogoutAllAsync(Guid userId, CancellationToken ct = default)
    {
        // Block all active session family IDs immediately so current access tokens get a 401.
        var active = await _tokens.GetActiveByUserIdAsync(userId, ct);
        foreach (var t in active)
            _revokedJtis.Revoke(t.FamilyId.ToString(), TimeSpan.FromMinutes(20));

        await _tokens.RevokeAllByUserIdAsync(userId, "Logout all", ct);
        await _realtimeCloser.CloseUserConnectionsAsync(userId, "auth.session_revoked", ct);
        _logger.LogInformation("Security: Logout-all. UserId={UserId}", userId);
        return Result.Ok();
    }

    public async Task<Result<UserDto>> GetCurrentUserAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user == null)
            return Result<UserDto>.Fail(Error.NotFound);

        return Result<UserDto>.Ok(ToDto(user));
    }

    public async Task<Result> SendVerificationEmailAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user == null)
            return Result.Fail(Error.NotFound);

        if (user.IsEmailVerified)
            return Result.Fail("Auth.AlreadyVerified", "This email address is already verified.");

        // Enforce per-user 60-second cooldown to prevent abuse.
        var latest = await _verificationTokens.GetLatestByUserIdAsync(userId, ct);
        if (latest != null && (DateTime.UtcNow - latest.CreatedAt).TotalSeconds < 60)
            return Result.Fail("Auth.ResendTooSoon", "Please wait before requesting another verification email.");

        await SendVerificationEmailCoreAsync(user, ct);
        return Result.Ok();
    }

    public async Task<Result> VerifyEmailAsync(string rawToken, CancellationToken ct = default)
    {
        var tokenHash = _tokenService.HashToken(rawToken);
        var stored = await _verificationTokens.GetByHashAsync(tokenHash, ct);

        if (stored == null || !stored.IsValid)
            return Result.Fail("Auth.InvalidToken", "This verification link is invalid or has expired.");

        var user = await _users.GetByIdAsync(stored.UserId, ct);
        if (user == null)
            return Result.Fail("Auth.InvalidToken", "Verification link is invalid.");

        stored.MarkUsed();
        await _verificationTokens.UpdateAsync(stored, ct);

        if (!string.IsNullOrEmpty(stored.PendingEmail))
        {
            // Email-change confirmation flow.
            user.UpdateEmail(stored.PendingEmail);
            await _users.UpdateAsync(user, ct);
            _logger.LogInformation("Security: Email changed. UserId={UserId}", user.Id);
            return Result.Ok();
        }

        user.VerifyEmail();
        await _users.UpdateAsync(user, ct);

        _logger.LogInformation("Security: Email verified. UserId={UserId}", user.Id);
        try { await _email.SendWelcomeEmailAsync(user.Email, user.DisplayName, ct); }
        catch { /* non-fatal */ }

        return Result.Ok();
    }

    public async Task<Result> ForgotPasswordAsync(string email, CancellationToken ct = default)
    {
        var normalized = email.Trim().ToUpperInvariant();
        var user = await _users.GetByNormalizedEmailAsync(normalized, ct);

        // Always return Ok to prevent user enumeration.
        if (user == null)
            return Result.Ok();

        // Invalidate any existing reset tokens before issuing a new one.
        await _resetTokens.InvalidateAllForUserAsync(user.Id, ct);

        var rawToken = _tokenService.GenerateRawRefreshToken();
        var tokenHash = _tokenService.HashToken(rawToken);
        var resetToken = PasswordResetToken.Create(user.Id, tokenHash);
        await _resetTokens.AddAsync(resetToken, ct);

        var resetUrl = $"{_emailOptions.AppUrl}/reset-password?token={Uri.EscapeDataString(rawToken)}";

        _logger.LogInformation("Security: Password reset requested. UserId={UserId}", user.Id);
        try { await _email.SendPasswordResetEmailAsync(user.Email, user.DisplayName, resetUrl, ct); }
        catch { /* non-fatal — we return Ok regardless */ }

        return Result.Ok();
    }

    public async Task<Result> ResetPasswordAsync(string rawToken, string newPassword, CancellationToken ct = default)
    {
        var tokenHash = _tokenService.HashToken(rawToken);
        var stored = await _resetTokens.GetByHashAsync(tokenHash, ct);

        if (stored == null || !stored.IsValid)
            return Result.Fail("Auth.InvalidToken", "This password reset link is invalid or has expired.");

        var user = await _users.GetByIdAsync(stored.UserId, ct);
        if (user == null)
            return Result.Fail("Auth.InvalidToken", "Password reset link is invalid.");

        stored.MarkUsed();
        await _resetTokens.UpdateAsync(stored, ct);

        user.UpdatePassword(_hasher.Hash(newPassword));
        await _users.UpdateAsync(user, ct);

        // Revoke all sessions so no old device stays signed in after a password reset.
        await _tokens.RevokeAllByUserIdAsync(user.Id, "Password reset", ct);

        _logger.LogInformation("Security: Password reset completed. UserId={UserId}", user.Id);
        try { await _email.SendPasswordChangedEmailAsync(user.Email, user.DisplayName, ct); }
        catch { /* non-fatal */ }

        return Result.Ok();
    }

    // ──────────────────────────────────────────────────────────────────────────

    private async Task<string> GenerateUniqueUsernameAsync(string hint, CancellationToken ct)
    {
        // Sanitize: keep alphanumeric + underscore, lowercase, max 24 chars for the base.
        var sanitized = new string(hint.ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());

        if (sanitized.Length < 3)
            sanitized = "user";

        if (sanitized.Length > 24)
            sanitized = sanitized[..24];

        // Try the sanitized name first, then append random digits to resolve collisions.
        var candidate = sanitized;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!await _users.ExistsByUsernameAsync(candidate.ToUpperInvariant(), ct))
                return candidate;

            candidate = sanitized + Random.Shared.Next(1000, 9999).ToString();
        }

        // Last resort: guaranteed unique suffix.
        return "user" + Guid.NewGuid().ToString("N")[..8];
    }

    private async Task SendVerificationEmailCoreAsync(User user, CancellationToken ct)
    {
        await _verificationTokens.InvalidateAllForUserAsync(user.Id, ct);

        var rawToken = _tokenService.GenerateRawRefreshToken();
        var tokenHash = _tokenService.HashToken(rawToken);
        var verificationToken = EmailVerificationToken.Create(user.Id, tokenHash);
        await _verificationTokens.AddAsync(verificationToken, ct);

        var verifyUrl = $"{_emailOptions.AppUrl}/verify-email/confirm?token={Uri.EscapeDataString(rawToken)}";
        await _email.SendVerificationEmailAsync(user.Email, user.DisplayName, verifyUrl, ct);
    }

    private async Task<AuthTokenResult> IssueTokenPairAsync(
        User user, string ipAddress, string? userAgent, CancellationToken ct, Guid? familyId = null)
    {
        var sessionFamilyId = familyId ?? Guid.NewGuid();
        var (accessToken, expiresAt, _) = _tokenService.GenerateAccessToken(
            user.Id, user.Username, user.Role.ToString(), sessionFamilyId);

        var rawRefreshToken = _tokenService.GenerateRawRefreshToken();
        var tokenHash = _tokenService.HashToken(rawRefreshToken);

        var refreshToken = RefreshToken.Create(
            userId: user.Id,
            tokenHash: tokenHash,
            familyId: sessionFamilyId,
            expiresAt: DateTime.UtcNow.AddDays(_authOptions.RefreshTokenExpiryDays),
            createdByIp: ipAddress,
            userAgent: userAgent);

        await _tokens.AddAsync(refreshToken, ct);

        return new AuthTokenResult(accessToken, rawRefreshToken, expiresAt, ToDto(user));
    }

    public async Task<Result> ChangePasswordAsync(
        Guid userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null) return Result.Fail("General.NotFound", "User not found.");

        if (string.IsNullOrEmpty(user.PasswordHash))
            return Result.Fail("Auth.GoogleOnly", "This account uses Google sign-in. Use 'Forgot password' to set a password.");

        if (!_hasher.Verify(currentPassword, user.PasswordHash))
        {
            _logger.LogWarning("ChangePassword: incorrect current password for user {UserId}", userId);
            return Result.Fail("Auth.InvalidCredentials", "Current password is incorrect.");
        }

        var newHash = _hasher.Hash(newPassword);
        user.UpdatePassword(newHash);
        await _users.UpdateAsync(user, ct);

        // Revoke all sessions so the user must log in again with the new password.
        await _tokens.RevokeAllByUserIdAsync(userId, "password_changed", ct);
        _logger.LogInformation("Password changed for user {UserId}", userId);
        return Result.Ok();
    }

    public async Task<Result> RequestEmailChangeAsync(
        Guid userId, string newEmail, CancellationToken ct = default)
    {
        var normalizedNew = newEmail.Trim().ToUpperInvariant();
        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null) return Result.Fail("General.NotFound", "User not found.");

        if (user.NormalizedEmail == normalizedNew)
            return Result.Fail("Auth.EmailUnchanged", "New email is the same as the current email.");

        if (await _users.ExistsByEmailAsync(normalizedNew, ct))
            return Result.Fail("Auth.EmailTaken", "That email address is already in use.");

        // Re-use the verification token flow with PendingEmail so VerifyEmailAsync applies the change.
        await _verificationTokens.InvalidateAllForUserAsync(userId, ct);
        var rawToken = _tokenService.GenerateRawRefreshToken();
        var tokenHash = _tokenService.HashToken(rawToken);
        var verificationToken = EmailVerificationToken.Create(userId, tokenHash, pendingEmail: newEmail.Trim());
        await _verificationTokens.AddAsync(verificationToken, ct);

        var verifyUrl = $"{_emailOptions.AppUrl}/verify-email/confirm?token={Uri.EscapeDataString(rawToken)}";
        await _email.SendVerificationEmailAsync(newEmail.Trim(), user.DisplayName, verifyUrl, ct);
        _logger.LogInformation("Email-change verification sent for user {UserId}", userId);
        return Result.Ok();
    }

    public async Task<Result<IReadOnlyList<SessionDto>>> GetActiveSessionsAsync(
        Guid userId, string? rawRefreshToken, CancellationToken ct = default)
    {
        var currentHash = rawRefreshToken is not null ? _tokenService.HashToken(rawRefreshToken) : null;
        var tokens = await _tokens.GetActiveByUserIdAsync(userId, ct);
        var dtos = tokens.Select(t => new SessionDto(
            Id: t.Id,
            IpAddress: t.CreatedByIp,
            UserAgent: t.UserAgent,
            CreatedAt: t.CreatedAt,
            ExpiresAt: t.ExpiresAt,
            IsCurrent: currentHash is not null && t.TokenHash == currentHash))
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
        return Result<IReadOnlyList<SessionDto>>.Ok(dtos);
    }

    public async Task<Result> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var token = await _tokens.GetByIdAsync(sessionId, ct);
        if (token is null || token.UserId != userId)
            return Result.Fail("General.NotFound", "Session not found.");
        if (!token.IsActive)
            return Result.Fail("Auth.SessionInactive", "Session is already inactive.");

        // Block the session's family ID so the current access token gets a 401 on the next request,
        // rather than waiting up to 15 minutes for it to expire naturally.
        _revokedJtis.Revoke(token.FamilyId.ToString(), TimeSpan.FromMinutes(20));

        token.Revoke(string.Empty, "user_revoked");
        await _tokens.UpdateAsync(token, ct);
        await _realtimeCloser.CloseUserConnectionsAsync(userId, "auth.session_revoked", ct);
        _logger.LogInformation("Session {SessionId} revoked by user {UserId}", sessionId, userId);
        return Result.Ok();
    }

    public async Task<Result> DeleteAccountAsync(Guid userId, string password, CancellationToken ct = default)
    {
        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null) return Result.Fail("General.NotFound", "User not found.");

        if (!string.IsNullOrEmpty(user.PasswordHash) && !_hasher.Verify(password, user.PasswordHash))
            return Result.Fail("Auth.InvalidCredentials", "Password is incorrect.");

        await _tokens.RevokeAllByUserIdAsync(userId, "account_deleted", ct);
        await _users.DeleteAsync(user, ct);
        await _realtimeCloser.CloseUserConnectionsAsync(userId, "auth.session_revoked", ct);
        _logger.LogInformation("Account deleted for user {UserId}", userId);
        return Result.Ok();
    }

    private static UserDto ToDto(User user) => new(
        user.Id,
        user.Username,
        user.DisplayName,
        user.Email,
        user.Initials,
        user.Color,
        user.Role.ToString(),
        user.IsEmailVerified,
        user.CreatedAt);
}
