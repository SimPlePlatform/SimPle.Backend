using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SimPle.Application.Auth.DTOs;
using SimPle.Application.Auth.Services;
using SimPle.Application.Auth.Validators;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Domain.Users;

namespace SimPle.UnitTests.Auth;

public sealed class AccountSecurityTests
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

    public AccountSecurityTests()
    {
        _tokenService.GenerateAccessToken(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>())
            .Returns(_ => ("access-token", DateTime.UtcNow.AddMinutes(15), "test-jti"));
        _tokenService.GenerateRawRefreshToken().Returns("raw-token");
        _tokenService.HashToken(Arg.Any<string>()).Returns(call => "hash-" + call.Arg<string>());

        _service = new AuthService(
            _users, _tokens, _verificationTokens, _resetTokens,
            _hasher, _tokenService, _emailService, _googleValidator,
            _revokedJtis,
            _realtimeCloser,
            Options.Create(new AuthOptions { RefreshTokenExpiryDays = 7, MaxFailedLoginAttempts = 10, LockoutDurationMinutes = 15 }),
            Options.Create(new EmailOptions { AppUrl = "http://localhost:3000", SmtpHost = "smtp.test", Password = "x" }),
            NullLogger<AuthService>.Instance);
    }

    private static User MakeUser(string passwordHash = "hashed-pw", string? googleId = null)
    {
        var u = User.Create("testuser", "test@example.com", passwordHash, "Test User");
        if (googleId is not null) u.ConnectGoogle(googleId);
        return u;
    }

    // ── ChangePassword ────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangePassword_CorrectCurrentPassword_Succeeds()
    {
        var user = MakeUser("hashed-pw");
        _users.GetByIdAsync(user.Id).Returns(user);
        _hasher.Verify("current-pw", "hashed-pw").Returns(true);
        _hasher.Hash("New-pw-1").Returns("new-hashed-pw");
        _tokens.RevokeAllByUserIdAsync(user.Id, Arg.Any<string>()).Returns(Task.CompletedTask);

        var result = await _service.ChangePasswordAsync(user.Id, "current-pw", "New-pw-1");

        result.IsSuccess.Should().BeTrue();
        await _users.Received(1).UpdateAsync(Arg.Is<User>(u => u.Id == user.Id));
        await _tokens.Received(1).RevokeAllByUserIdAsync(user.Id, "password_changed");
    }

    [Fact]
    public async Task ChangePassword_WrongCurrentPassword_Fails()
    {
        var user = MakeUser("hashed-pw");
        _users.GetByIdAsync(user.Id).Returns(user);
        _hasher.Verify("wrong-pw", "hashed-pw").Returns(false);

        var result = await _service.ChangePasswordAsync(user.Id, "wrong-pw", "New-pw-1");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Auth.InvalidCredentials");
    }

    [Fact]
    public async Task ChangePassword_GoogleOnlyAccount_Fails()
    {
        var user = User.CreateWithGoogle("guser", "g@gmail.com", "G User", "gid-123", null);
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.ChangePasswordAsync(user.Id, "any", "New-pw-1");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Auth.GoogleOnly");
    }

    [Fact]
    public async Task ChangePassword_UserNotFound_Fails()
    {
        _users.GetByIdAsync(Arg.Any<Guid>()).Returns((User?)null);

        var result = await _service.ChangePasswordAsync(Guid.NewGuid(), "pw", "New-pw-1");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("General.NotFound");
    }

    // ── ChangePasswordValidator ───────────────────────────────────────────────

    [Theory]
    [InlineData("", "Valid1234", "Valid1234")]        // empty current
    [InlineData("current", "short", "short")]         // too short
    [InlineData("current", "Valid1234", "mismatch")]  // confirm mismatch
    [InlineData("current", "current", "current")]     // same as current
    public async Task ChangePasswordValidator_InvalidInput_HasErrors(
        string current, string newPw, string confirm)
    {
        var validator = new ChangePasswordRequestValidator();
        var dto = new ChangePasswordRequestDto(current, newPw, confirm);
        var result = await validator.ValidateAsync(dto, CancellationToken.None);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task ChangePasswordValidator_ValidInput_Passes()
    {
        var validator = new ChangePasswordRequestValidator();
        var dto = new ChangePasswordRequestDto("current-pw", "NewPassword1", "NewPassword1");
        var result = await validator.ValidateAsync(dto, CancellationToken.None);
        result.IsValid.Should().BeTrue();
    }

    // ── RequestEmailChange ───────────────────────────────────────────────────

    [Fact]
    public async Task RequestEmailChange_NewEmail_SendsVerification()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);
        _users.ExistsByEmailAsync(Arg.Any<string>()).Returns(false);

        var result = await _service.RequestEmailChangeAsync(user.Id, "new@example.com");

        result.IsSuccess.Should().BeTrue();
        await _verificationTokens.Received(1).AddAsync(
            Arg.Is<EmailVerificationToken>(t => t.PendingEmail == "new@example.com"));
        await _emailService.Received(1).SendVerificationEmailAsync(
            "new@example.com", Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task RequestEmailChange_SameEmail_Fails()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.RequestEmailChangeAsync(user.Id, "test@example.com");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Auth.EmailUnchanged");
    }

    [Fact]
    public async Task RequestEmailChange_EmailAlreadyTaken_Fails()
    {
        var user = MakeUser();
        _users.GetByIdAsync(user.Id).Returns(user);
        _users.ExistsByEmailAsync(Arg.Any<string>()).Returns(true);

        var result = await _service.RequestEmailChangeAsync(user.Id, "taken@example.com");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Auth.EmailTaken");
    }

    // ── GetActiveSessions ────────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveSessions_ReturnsMappedDtos()
    {
        var userId = Guid.NewGuid();
        var token = RefreshToken.Create(userId, "hash-raw-token", Guid.NewGuid(),
            DateTime.UtcNow.AddDays(7), "1.2.3.4", "Mozilla/5.0");

        _tokens.GetActiveByUserIdAsync(userId).Returns(
            (IReadOnlyList<RefreshToken>)[token]);
        _tokenService.HashToken("raw-token").Returns("hash-raw-token");

        var result = await _service.GetActiveSessionsAsync(userId, "raw-token");

        result.IsSuccess.Should().BeTrue();
        var sessions = result.Value!;
        sessions.Should().HaveCount(1);
        sessions[0].IsCurrent.Should().BeTrue();
        sessions[0].IpAddress.Should().Be("1.2.3.4");
    }

    [Fact]
    public async Task GetActiveSessions_NullRawToken_IsCurrent_False()
    {
        var userId = Guid.NewGuid();
        var token = RefreshToken.Create(userId, "hash-raw-token", Guid.NewGuid(),
            DateTime.UtcNow.AddDays(7), "1.2.3.4", null);
        _tokens.GetActiveByUserIdAsync(userId).Returns(
            (IReadOnlyList<RefreshToken>)[token]);

        var result = await _service.GetActiveSessionsAsync(userId, null);

        result.Value![0].IsCurrent.Should().BeFalse();
    }

    // ── RevokeSession ────────────────────────────────────────────────────────

    [Fact]
    public async Task RevokeSession_ValidSession_Revokes()
    {
        var userId = Guid.NewGuid();
        var token = RefreshToken.Create(userId, "hash-abc", Guid.NewGuid(),
            DateTime.UtcNow.AddDays(7), "1.2.3.4", null);
        _tokens.GetByIdAsync(token.Id).Returns(token);

        var result = await _service.RevokeSessionAsync(userId, token.Id);

        result.IsSuccess.Should().BeTrue();
        await _tokens.Received(1).UpdateAsync(Arg.Is<RefreshToken>(t => t.IsRevoked));
    }

    [Fact]
    public async Task RevokeSession_NotFound_Fails()
    {
        _tokens.GetByIdAsync(Arg.Any<Guid>()).Returns((RefreshToken?)null);

        var result = await _service.RevokeSessionAsync(Guid.NewGuid(), Guid.NewGuid());

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("General.NotFound");
    }

    [Fact]
    public async Task RevokeSession_WrongOwner_Fails()
    {
        var token = RefreshToken.Create(Guid.NewGuid(), "hash-abc", Guid.NewGuid(),
            DateTime.UtcNow.AddDays(7), "1.2.3.4", null);
        _tokens.GetByIdAsync(token.Id).Returns(token);

        var result = await _service.RevokeSessionAsync(Guid.NewGuid(), token.Id);

        result.IsSuccess.Should().BeFalse();
    }

    // ── DeleteAccount ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAccount_CorrectPassword_DeletesUser()
    {
        var user = MakeUser("hashed-pw");
        _users.GetByIdAsync(user.Id).Returns(user);
        _hasher.Verify("correct-pw", "hashed-pw").Returns(true);

        var result = await _service.DeleteAccountAsync(user.Id, "correct-pw");

        result.IsSuccess.Should().BeTrue();
        await _tokens.Received(1).RevokeAllByUserIdAsync(user.Id, "account_deleted");
        await _users.Received(1).DeleteAsync(user);
    }

    [Fact]
    public async Task DeleteAccount_WrongPassword_Fails()
    {
        var user = MakeUser("hashed-pw");
        _users.GetByIdAsync(user.Id).Returns(user);
        _hasher.Verify("wrong-pw", "hashed-pw").Returns(false);

        var result = await _service.DeleteAccountAsync(user.Id, "wrong-pw");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("Auth.InvalidCredentials");
        await _users.DidNotReceive().DeleteAsync(Arg.Any<User>());
    }

    [Fact]
    public async Task DeleteAccount_GoogleOnlyAccount_NoPasswordCheck_Succeeds()
    {
        // Google-only users have empty PasswordHash — password check is skipped.
        var user = User.CreateWithGoogle("guser", "g@gmail.com", "G User", "gid-123", null);
        _users.GetByIdAsync(user.Id).Returns(user);

        var result = await _service.DeleteAccountAsync(user.Id, string.Empty);

        result.IsSuccess.Should().BeTrue();
        await _users.Received(1).DeleteAsync(user);
    }
}
