using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SimPle.Application.Auth.DTOs;
using SimPle.Application.Auth.Services;
using SimPle.Application.Auth.Validators;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Domain.Users;

namespace SimPle.UnitTests.Auth;

public sealed class AuthServiceTests
{
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IRefreshTokenRepository _tokens = Substitute.For<IRefreshTokenRepository>();
    private readonly IEmailVerificationTokenRepository _verificationTokens = Substitute.For<IEmailVerificationTokenRepository>();
    private readonly IPasswordResetTokenRepository _resetTokens = Substitute.For<IPasswordResetTokenRepository>();
    private readonly IPasswordHashingService _hasher = Substitute.For<IPasswordHashingService>();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IGoogleTokenValidationService _googleValidator = Substitute.For<IGoogleTokenValidationService>();
    private readonly IRevokedJtiStore _revokedJtis = Substitute.For<IRevokedJtiStore>();
    private readonly IRealtimeConnectionCloser _realtimeCloser = Substitute.For<IRealtimeConnectionCloser>();
    private readonly AuthService _service;

    public AuthServiceTests()
    {
        _hasher.Hash(Arg.Any<string>()).Returns("stored-password-hash");
        _tokenService.GenerateAccessToken(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>())
            .Returns(_ => ("access-token", DateTime.UtcNow.AddMinutes(15), "test-jti"));
        _tokenService.GenerateRawRefreshToken().Returns("new-raw-token");
        _tokenService.HashToken(Arg.Any<string>())
            .Returns(call => "hash-" + call.Arg<string>());

        _service = new AuthService(
            _users,
            _tokens,
            _verificationTokens,
            _resetTokens,
            _hasher,
            _tokenService,
            _emailService,
            _googleValidator,
            _revokedJtis,
            _realtimeCloser,
            Options.Create(new AuthOptions
            {
                RefreshTokenExpiryDays = 7,
                MaxFailedLoginAttempts = 2,
                LockoutDurationMinutes = 30
            }),
            Options.Create(new EmailOptions
            {
                AppUrl = "http://localhost:3000",
                SmtpHost = "smtp.example.com",
                Password = "test"
            }),
            NullLogger<AuthService>.Instance);
    }

    // ── Register ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Register_CreatesUserWithHashedPassword()
    {
        var request = ValidRegisterRequest();

        var result = await _service.RegisterAsync(request, "", null);

        result.IsSuccess.Should().BeTrue();
        result.Value!.User.Email.Should().Be(request.Email);
        await _users.Received(1).AddAsync(Arg.Is<User>(u =>
            u.NormalizedEmail == "MOHAN@EXAMPLE.COM" &&
            u.PasswordHash == "stored-password-hash"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Register_RejectsDuplicateEmailOrUsername()
    {
        _users.ExistsByEmailAsync("MOHAN@EXAMPLE.COM", Arg.Any<CancellationToken>()).Returns(true);
        var emailTaken = await _service.RegisterAsync(ValidRegisterRequest(), "", null);

        _users.ExistsByEmailAsync("MOHAN@EXAMPLE.COM", Arg.Any<CancellationToken>()).Returns(false);
        _users.ExistsByUsernameAsync("MOHAN", Arg.Any<CancellationToken>()).Returns(true);
        var usernameTaken = await _service.RegisterAsync(ValidRegisterRequest(), "", null);

        emailTaken.Error!.Code.Should().Be("Auth.EmailTaken");
        usernameTaken.Error!.Code.Should().Be("Auth.UsernameTaken");
    }

    [Fact]
    public async Task Register_AttemptsToSendVerificationEmail()
    {
        await _service.RegisterAsync(ValidRegisterRequest(), "", null);

        await _emailService.Received(1).SendVerificationEmailAsync(
            "mohan@example.com",
            Arg.Any<string>(),
            Arg.Is<string>(url => url.Contains("/verify-email/confirm?token=")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Register_SucceedsEvenWhenVerificationEmailThrows()
    {
        _emailService.SendVerificationEmailAsync(Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("SMTP unavailable"));

        var result = await _service.RegisterAsync(ValidRegisterRequest(), "", null);

        result.IsSuccess.Should().BeTrue("registration must succeed regardless of email failure");
        result.Value!.User.Email.Should().Be("mohan@example.com");
    }

    [Fact]
    public async Task Register_CreatesEmailVerifiedFalseByDefault()
    {
        await _service.RegisterAsync(ValidRegisterRequest(), "", null);

        await _users.Received(1).AddAsync(
            Arg.Is<User>(u => !u.IsEmailVerified),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void RegistrationValidation_RejectsWeakOrMismatchedPasswords()
    {
        var validator = new RegisterRequestValidator();

        var weak = validator.Validate(new RegisterRequestDto("mohan", "mohan@example.com",
            "alllowercase", "alllowercase", "test-captcha"));
        var mismatch = validator.Validate(new RegisterRequestDto("mohan", "mohan@example.com",
            "ValidPass1!", "DifferentPass1!", "test-captcha"));

        weak.IsValid.Should().BeFalse();
        mismatch.IsValid.Should().BeFalse();
    }

    // ── EmailExists ───────────────────────────────────────────────────────────

    [Fact]
    public async Task EmailExists_ReturnsTrueWhenEmailRegistered()
    {
        _users.ExistsByEmailAsync("MOHAN@EXAMPLE.COM", Arg.Any<CancellationToken>()).Returns(true);

        var result = await _service.EmailExistsAsync("mohan@example.com");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task EmailExists_ReturnsFalseWhenEmailNotRegistered()
    {
        _users.ExistsByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _service.EmailExistsAsync("unknown@example.com");

        result.Should().BeFalse();
    }

    // ── Login ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_ReturnsUserAndIssuesRefreshTokenForValidPassword()
    {
        var user = ExistingUser();
        _users.GetByNormalizedEmailOrUsernameAsync("MOHAN", Arg.Any<CancellationToken>()).Returns(user);
        _hasher.Verify("valid password phrase", user.PasswordHash).Returns(true);

        var result = await _service.LoginAsync(
            new LoginRequestDto("mohan", "valid password phrase"), "127.0.0.1", "tests");

        result.IsSuccess.Should().BeTrue();
        result.Value!.AccessToken.Should().Be("access-token");
        await _tokens.Received(1).AddAsync(Arg.Is<RefreshToken>(t =>
            t.TokenHash == "hash-new-raw-token" &&
            t.UserId == user.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Login_RehashesPasswordWhenStoredParametersNeedUpgrade()
    {
        var user = ExistingUser();
        _users.GetByNormalizedEmailOrUsernameAsync("MOHAN", Arg.Any<CancellationToken>()).Returns(user);
        _hasher.Verify("valid password phrase", user.PasswordHash).Returns(true);
        _hasher.NeedsRehash(user.PasswordHash).Returns(true);
        _hasher.Hash("valid password phrase").Returns("upgraded-password-hash");

        await _service.LoginAsync(
            new LoginRequestDto("mohan", "valid password phrase"), "", null);

        user.PasswordHash.Should().Be("upgraded-password-hash");
    }

    [Fact]
    public async Task Login_UsesGenericFailureForWrongPasswordAndUnknownUser()
    {
        var user = ExistingUser();
        _users.GetByNormalizedEmailOrUsernameAsync("MOHAN", Arg.Any<CancellationToken>()).Returns(user);
        _hasher.Verify("wrong password", user.PasswordHash).Returns(false);

        var wrongPassword = await _service.LoginAsync(
            new LoginRequestDto("mohan", "wrong password"), "", null);
        var missingUser = await _service.LoginAsync(
            new LoginRequestDto("missing", "wrong password"), "", null);

        wrongPassword.Error!.Message.Should().Be("Invalid email/username or password.");
        missingUser.Error!.Message.Should().Be(wrongPassword.Error.Message);
    }

    [Fact]
    public async Task Login_LocksOutAtConfiguredAttemptLimit()
    {
        var user = ExistingUser();
        _users.GetByNormalizedEmailOrUsernameAsync("MOHAN", Arg.Any<CancellationToken>()).Returns(user);
        _hasher.Verify(Arg.Any<string>(), user.PasswordHash).Returns(false);

        await _service.LoginAsync(new LoginRequestDto("mohan", "bad-one"), "", null);
        await _service.LoginAsync(new LoginRequestDto("mohan", "bad-two"), "", null);
        var locked = await _service.LoginAsync(new LoginRequestDto("mohan", "valid password phrase"), "", null);

        user.IsLockedOut().Should().BeTrue();
        locked.Error!.Code.Should().Be("Auth.LockedOut");
        _hasher.Received(2).Verify(Arg.Any<string>(), user.PasswordHash);
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_RotatesAnActiveTokenWithinItsFamily()
    {
        var user = ExistingUser();
        var familyId = Guid.NewGuid();
        var oldToken = RefreshToken.Create(user.Id, "hash-old-token", familyId,
            DateTime.UtcNow.AddDays(1), "127.0.0.1", null);
        _tokens.GetByHashAsync("hash-old-token", Arg.Any<CancellationToken>()).Returns(oldToken);
        _tokens.TryUpdateAsync(Arg.Any<RefreshToken>(), Arg.Any<CancellationToken>()).Returns(true);
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _service.RefreshAsync("old-token", "127.0.0.1");

        result.IsSuccess.Should().BeTrue();
        oldToken.IsRevoked.Should().BeTrue();
        await _tokens.Received(1).AddAsync(Arg.Is<RefreshToken>(t =>
            t.FamilyId == familyId && t.TokenHash == "hash-new-raw-token"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refresh_RejectsMissingOrExpiredToken()
    {
        var expired = RefreshToken.Create(Guid.NewGuid(), "hash-expired-token", Guid.NewGuid(),
            DateTime.UtcNow.AddMinutes(-1), "", null);
        _tokens.GetByHashAsync("hash-expired-token", Arg.Any<CancellationToken>()).Returns(expired);

        var missing = await _service.RefreshAsync("missing-token", "");
        var expiredResult = await _service.RefreshAsync("expired-token", "");

        missing.Error!.Code.Should().Be("Auth.InvalidToken");
        expiredResult.Error!.Code.Should().Be("Auth.InvalidToken");
    }

    [Fact]
    public async Task Refresh_RevokesFamilyWhenRotatedTokenIsReused()
    {
        var oldToken = RefreshToken.Create(Guid.NewGuid(), "hash-old-token", Guid.NewGuid(),
            DateTime.UtcNow.AddDays(1), "", null);
        oldToken.Revoke("", "Rotated");
        _tokens.GetByHashAsync("hash-old-token", Arg.Any<CancellationToken>()).Returns(oldToken);

        var result = await _service.RefreshAsync("old-token", "");

        result.Error!.Code.Should().Be("Auth.TokenReuse");
        await _tokens.Received(1).RevokeAllByFamilyIdAsync(
            oldToken.FamilyId, "Reuse detected", Arg.Any<CancellationToken>());
    }

    // ── Logout ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LogoutAndLogoutAll_RevokeAppropriateSessions()
    {
        var user = ExistingUser();
        var token = RefreshToken.Create(user.Id, "hash-old-token", Guid.NewGuid(),
            DateTime.UtcNow.AddDays(1), "", null);
        _tokens.GetByHashAsync("hash-old-token", Arg.Any<CancellationToken>()).Returns(token);

        await _service.LogoutAsync("old-token");
        await _service.LogoutAllAsync(user.Id);

        token.IsRevoked.Should().BeTrue();
        await _tokens.Received(1).RevokeAllByUserIdAsync(
            user.Id, "Logout all", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression test for the Module 7 fix (docs/specs/module-07-realtime-presence-chat-spec.md): before this
    /// fix, LogoutAsync revoked only the refresh-token row, never the session family — so a session logged out via
    /// this path could still authenticate with a not-yet-expired access token cookie until it expired naturally
    /// (LogoutAllAsync/RevokeSessionAsync already revoked the family; LogoutAsync did not). This also closes any
    /// live realtime connections proactively, since a realtime hub connection would otherwise survive "logout"
    /// indefinitely (see "the load-bearing rule" in the spec).
    /// </summary>
    [Fact]
    public async Task LogoutAsync_RevokesSessionFamilyAndClosesRealtimeConnections()
    {
        var user = ExistingUser();
        var token = RefreshToken.Create(user.Id, "hash-old-token", Guid.NewGuid(),
            DateTime.UtcNow.AddDays(1), "", null);
        _tokens.GetByHashAsync("hash-old-token", Arg.Any<CancellationToken>()).Returns(token);

        await _service.LogoutAsync("old-token");

        token.IsRevoked.Should().BeTrue();
        _revokedJtis.Received(1).Revoke(token.FamilyId.ToString(), Arg.Any<TimeSpan>());
        await _realtimeCloser.Received(1).CloseUserConnectionsAsync(
            user.Id, "auth.session_revoked", Arg.Any<CancellationToken>());
    }

    // ── GetCurrentUser ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCurrentUser_ReturnsSafeDtoOrNotFound()
    {
        var user = ExistingUser();
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);

        var found = await _service.GetCurrentUserAsync(user.Id);
        var missing = await _service.GetCurrentUserAsync(Guid.NewGuid());

        found.Value!.Email.Should().Be("mohan@example.com");
        missing.Error!.Code.Should().Be("General.NotFound");
    }

    // ── SendVerificationEmail ─────────────────────────────────────────────────

    [Fact]
    public async Task SendVerificationEmail_FailsForNonExistentUser()
    {
        _users.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _service.SendVerificationEmailAsync(Guid.NewGuid());

        result.Error!.Code.Should().Be("General.NotFound");
        await _emailService.DidNotReceive().SendVerificationEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendVerificationEmail_FailsWhenEmailAlreadyVerified()
    {
        var user = ExistingUser();
        user.VerifyEmail();
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _service.SendVerificationEmailAsync(user.Id);

        result.Error!.Code.Should().Be("Auth.AlreadyVerified");
        await _emailService.DidNotReceive().SendVerificationEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendVerificationEmail_BlocksResendWithinSixtySecondCooldown()
    {
        var user = ExistingUser();
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        var recentToken = EmailVerificationToken.Create(user.Id, "hash-recent");
        // CreatedAt defaults to UtcNow so it's well within the 60-second window.
        _verificationTokens.GetLatestByUserIdAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(recentToken);

        var result = await _service.SendVerificationEmailAsync(user.Id);

        result.Error!.Code.Should().Be("Auth.ResendTooSoon");
        await _emailService.DidNotReceive().SendVerificationEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendVerificationEmail_SendsEmailWhenNoPriorToken()
    {
        var user = ExistingUser();
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        _verificationTokens.GetLatestByUserIdAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns((EmailVerificationToken?)null);

        var result = await _service.SendVerificationEmailAsync(user.Id);

        result.IsSuccess.Should().BeTrue();
        await _emailService.Received(1).SendVerificationEmailAsync(
            user.Email,
            Arg.Any<string>(),
            Arg.Is<string>(url => url.Contains("/verify-email/confirm?token=")),
            Arg.Any<CancellationToken>());
        await _verificationTokens.Received(1).AddAsync(
            Arg.Is<EmailVerificationToken>(t => t.UserId == user.Id),
            Arg.Any<CancellationToken>());
    }

    // ── VerifyEmail ───────────────────────────────────────────────────────────

    [Fact]
    public async Task VerifyEmail_FailsForInvalidOrExpiredToken()
    {
        _verificationTokens.GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((EmailVerificationToken?)null);

        var missing = await _service.VerifyEmailAsync("bad-token");

        missing.Error!.Code.Should().Be("Auth.InvalidToken");

        // Used token
        var usedToken = EmailVerificationToken.Create(Guid.NewGuid(), "hash-used");
        usedToken.MarkUsed();
        _verificationTokens.GetByHashAsync("hash-new-raw-token", Arg.Any<CancellationToken>())
            .Returns(usedToken);

        var used = await _service.VerifyEmailAsync("new-raw-token");

        used.Error!.Code.Should().Be("Auth.InvalidToken");
    }

    [Fact]
    public async Task VerifyEmail_MarksUserVerifiedAndSendsWelcomeEmail()
    {
        var user = ExistingUser();
        var token = EmailVerificationToken.Create(user.Id, "hash-new-raw-token");
        _verificationTokens.GetByHashAsync("hash-new-raw-token", Arg.Any<CancellationToken>())
            .Returns(token);
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _service.VerifyEmailAsync("new-raw-token");

        result.IsSuccess.Should().BeTrue();
        token.IsValid.Should().BeFalse("token must be marked used");
        user.IsEmailVerified.Should().BeTrue();
        await _users.Received(1).UpdateAsync(
            Arg.Is<User>(u => u.IsEmailVerified), Arg.Any<CancellationToken>());
        await _emailService.Received(1).SendWelcomeEmailAsync(
            user.Email, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── ForgotPassword ────────────────────────────────────────────────────────

    [Fact]
    public async Task ForgotPassword_ReturnsOkForUnknownEmailWithoutLeaking()
    {
        _users.GetByNormalizedEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _service.ForgotPasswordAsync("unknown@example.com");

        result.IsSuccess.Should().BeTrue("anti-enumeration: always return Ok");
        await _emailService.DidNotReceive().SendPasswordResetEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _resetTokens.DidNotReceive().AddAsync(
            Arg.Any<PasswordResetToken>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForgotPassword_InvalidatesOldTokensAndSendsResetEmailForKnownUser()
    {
        var user = ExistingUser();
        _users.GetByNormalizedEmailAsync("MOHAN@EXAMPLE.COM", Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _service.ForgotPasswordAsync("mohan@example.com");

        result.IsSuccess.Should().BeTrue();
        await _resetTokens.Received(1).InvalidateAllForUserAsync(user.Id, Arg.Any<CancellationToken>());
        await _resetTokens.Received(1).AddAsync(
            Arg.Is<PasswordResetToken>(t => t.UserId == user.Id),
            Arg.Any<CancellationToken>());
        await _emailService.Received(1).SendPasswordResetEmailAsync(
            user.Email,
            Arg.Any<string>(),
            Arg.Is<string>(url => url.Contains("/reset-password?token=")),
            Arg.Any<CancellationToken>());
    }

    // ── ResetPassword ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetPassword_FailsForInvalidOrExpiredToken()
    {
        _resetTokens.GetByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((PasswordResetToken?)null);

        var result = await _service.ResetPasswordAsync("bad-token", "NewPass1!");

        result.Error!.Code.Should().Be("Auth.InvalidToken");
    }

    [Fact]
    public async Task ResetPassword_FailsForAlreadyUsedToken()
    {
        var user = ExistingUser();
        var usedToken = PasswordResetToken.Create(user.Id, "hash-new-raw-token");
        usedToken.MarkUsed();
        _resetTokens.GetByHashAsync("hash-new-raw-token", Arg.Any<CancellationToken>())
            .Returns(usedToken);

        var result = await _service.ResetPasswordAsync("new-raw-token", "NewPass1!");

        result.Error!.Code.Should().Be("Auth.InvalidToken");
    }

    [Fact]
    public async Task ResetPassword_UpdatesPasswordRevokesSessionsAndSendsNotification()
    {
        var user = ExistingUser();
        var resetToken = PasswordResetToken.Create(user.Id, "hash-new-raw-token");
        _resetTokens.GetByHashAsync("hash-new-raw-token", Arg.Any<CancellationToken>())
            .Returns(resetToken);
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        _hasher.Hash("NewPass1!").Returns("new-hash");

        var result = await _service.ResetPasswordAsync("new-raw-token", "NewPass1!");

        result.IsSuccess.Should().BeTrue();
        resetToken.IsValid.Should().BeFalse("token must be marked used");
        user.PasswordHash.Should().Be("new-hash");
        await _tokens.Received(1).RevokeAllByUserIdAsync(
            user.Id, "Password reset", Arg.Any<CancellationToken>());
        await _emailService.Received(1).SendPasswordChangedEmailAsync(
            user.Email, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Validator: ForgotPassword ─────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("missing@")]
    [InlineData("@nodomain.com")]
    public void ForgotPasswordValidation_RejectsInvalidEmails(string email)
    {
        var validator = new ForgotPasswordRequestValidator();

        var result = validator.Validate(new ForgotPasswordRequestDto(email));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ForgotPasswordValidation_AcceptsValidEmail()
    {
        var validator = new ForgotPasswordRequestValidator();

        var result = validator.Validate(new ForgotPasswordRequestDto("user@example.com"));

        result.IsValid.Should().BeTrue();
    }

    // ── Validator: ResetPassword ──────────────────────────────────────────────

    [Theory]
    [InlineData("12345678", "12345678")]   // only digits — single category
    [InlineData("alllowercase", "alllowercase")]  // only lowercase
    [InlineData("ALLUPPERCASE", "ALLUPPERCASE")]  // only uppercase
    public void ResetPasswordValidation_RejectsWeakPasswords(string password, string confirm)
    {
        var validator = new ResetPasswordRequestValidator();

        var result = validator.Validate(new ResetPasswordRequestDto("token", password, confirm));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ResetPasswordValidation_RejectsMismatchedPasswords()
    {
        var validator = new ResetPasswordRequestValidator();

        var result = validator.Validate(new ResetPasswordRequestDto(
            "token", "ValidPass1!", "DifferentPass1!"));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ResetPasswordValidation_RejectsEmptyToken()
    {
        var validator = new ResetPasswordRequestValidator();

        var result = validator.Validate(new ResetPasswordRequestDto("", "ValidPass1!", "ValidPass1!"));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ResetPasswordValidation_AcceptsValidRequest()
    {
        var validator = new ResetPasswordRequestValidator();

        var result = validator.Validate(new ResetPasswordRequestDto(
            "some-token", "ValidPass1!", "ValidPass1!"));

        result.IsValid.Should().BeTrue();
    }

    // ── Google OAuth ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GoogleLogin_ReturnsFailWhenTokenIsInvalid()
    {
        _googleValidator.ValidateAsync("bad-token", Arg.Any<CancellationToken>()).Returns((GoogleUserInfo?)null);

        var result = await _service.GoogleLoginAsync("bad-token", "1.2.3.4", null);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Auth.InvalidGoogleToken");
    }

    [Fact]
    public async Task GoogleLogin_CreatesNewUserWhenNeitherGoogleIdNorEmailExists()
    {
        var info = ValidGoogleInfo();
        _googleValidator.ValidateAsync("google-id-token", Arg.Any<CancellationToken>()).Returns(info);
        _users.GetByGoogleIdAsync(info.GoogleId, Arg.Any<CancellationToken>()).Returns((User?)null);
        _users.GetByNormalizedEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((User?)null);
        _users.ExistsByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _service.GoogleLoginAsync("google-id-token", "1.2.3.4", "Mozilla/5.0");

        result.IsSuccess.Should().BeTrue();
        await _users.Received(1).AddAsync(Arg.Is<User>(u =>
            u.GoogleId == info.GoogleId &&
            u.Email == info.Email &&
            u.IsEmailVerified), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GoogleLogin_NewUserEmailIsPreVerifiedFromGoogle()
    {
        var info = ValidGoogleInfo();
        _googleValidator.ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(info);
        _users.GetByGoogleIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((User?)null);
        _users.GetByNormalizedEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((User?)null);
        _users.ExistsByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _service.GoogleLoginAsync("token", "1.2.3.4", null);

        result.IsSuccess.Should().BeTrue();
        await _users.Received(1).AddAsync(
            Arg.Is<User>(u => u.IsEmailVerified == true),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GoogleLogin_LinksGoogleIdToExistingPasswordAccount()
    {
        var info = ValidGoogleInfo();
        var existingUser = ExistingUser();
        _googleValidator.ValidateAsync("token", Arg.Any<CancellationToken>()).Returns(info);
        _users.GetByGoogleIdAsync(info.GoogleId, Arg.Any<CancellationToken>()).Returns((User?)null);
        _users.GetByNormalizedEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(existingUser);

        var result = await _service.GoogleLoginAsync("token", "1.2.3.4", null);

        result.IsSuccess.Should().BeTrue();
        existingUser.GoogleId.Should().Be(info.GoogleId);
        await _users.DidNotReceive().AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
        await _users.Received().UpdateAsync(existingUser, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GoogleLogin_SignsInDirectlyWhenGoogleIdAlreadyLinked()
    {
        var info = ValidGoogleInfo();
        var googleUser = User.CreateWithGoogle("mohan_g", info.Email, info.DisplayName, info.GoogleId, null);
        _googleValidator.ValidateAsync("token", Arg.Any<CancellationToken>()).Returns(info);
        _users.GetByGoogleIdAsync(info.GoogleId, Arg.Any<CancellationToken>()).Returns(googleUser);

        var result = await _service.GoogleLoginAsync("token", "1.2.3.4", null);

        result.IsSuccess.Should().BeTrue();
        result.Value!.User.Email.Should().Be(info.Email);
        // Must not look up by email if the Google ID already resolved a user.
        await _users.DidNotReceive().GetByNormalizedEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GoogleLogin_UsernameCollisionAppendsDigits()
    {
        var info = ValidGoogleInfo();
        _googleValidator.ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(info);
        _users.GetByGoogleIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((User?)null);
        _users.GetByNormalizedEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((User?)null);
        // First call (base name) is taken; second call (with digits) is free.
        _users.ExistsByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true, false);

        var result = await _service.GoogleLoginAsync("token", "1.2.3.4", null);

        result.IsSuccess.Should().BeTrue();
        await _users.Received(1).AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GoogleLogin_IssuesRefreshTokenAndReturnsAccessToken()
    {
        var info = ValidGoogleInfo();
        _googleValidator.ValidateAsync("token", Arg.Any<CancellationToken>()).Returns(info);
        _users.GetByGoogleIdAsync(info.GoogleId, Arg.Any<CancellationToken>()).Returns((User?)null);
        _users.GetByNormalizedEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((User?)null);
        _users.ExistsByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await _service.GoogleLoginAsync("token", "1.2.3.4", null);

        result.IsSuccess.Should().BeTrue();
        result.Value!.AccessToken.Should().Be("access-token");
        result.Value!.RawRefreshToken.Should().Be("new-raw-token");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RegisterRequestDto ValidRegisterRequest() =>
        new("mohan", "mohan@example.com", "valid password phrase", "valid password phrase");

    private static User ExistingUser() =>
        User.Create("mohan", "mohan@example.com", "stored-password-hash", "Mohan");

    private static GoogleUserInfo ValidGoogleInfo() => new(
        GoogleId: "google-uid-12345",
        Email: "mohan@gmail.com",
        DisplayName: "Mohan Ehab",
        GivenName: "Mohan",
        PictureUrl: "https://lh3.googleusercontent.com/a/photo",
        EmailVerified: true);
}
