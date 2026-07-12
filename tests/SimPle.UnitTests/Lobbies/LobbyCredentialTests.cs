using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SimPle.Application.Common.Options;
using SimPle.Domain.Lobbies;
using SimPle.Infrastructure.Lobbies;

namespace SimPle.UnitTests.Lobbies;

/// <summary>
/// Join-credential construction, expiry, and rotation (brief Risk #7): keyed digests, plaintext revealed only at
/// creation/rotation, rotation invalidates the old value immediately, and using a credential never extends its
/// deadline.
/// </summary>
public class LobbyCredentialTests
{
    private readonly FakeTimeProvider _clock = new(LobbyTestFactory.T0);
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private static LobbyJoinCredential Issue(DateTime nowUtc, int generation = 1) =>
        LobbyJoinCredential.Issue(Guid.NewGuid(), "code-digest", "link-digest", generation, nowUtc);

    // ── The 30-minute expiry boundary ────────────────────────────────────────

    [Fact]
    public void ACredentialExpiresExactlyThirtyMinutesAfterIssue()
    {
        var credential = Issue(Now);

        credential.ExpiresAtUtc.Should().Be(LobbyTestFactory.T0.AddMinutes(30));
    }

    [Fact]
    public void JustBeforeThirtyMinutes_TheCredentialIsStillRedeemable()
    {
        var credential = Issue(Now);
        _clock.Advance(TimeSpan.FromMinutes(30) - TimeSpan.FromMilliseconds(1));

        credential.CanRedeem(Now).Should().BeTrue();
    }

    [Fact]
    public void ExactlyAtThirtyMinutes_TheCredentialIsNoLongerRedeemable()
    {
        var credential = Issue(Now);
        _clock.Advance(TimeSpan.FromMinutes(30));

        credential.IsExpired(Now).Should().BeTrue();
        credential.CanRedeem(Now).Should().BeFalse();
    }

    [Fact]
    public void RedeemingACredentialDoesNotExtendItsDeadline()
    {
        // "Using one does not extend either deadline" — a credential is consumed, never refreshed. If redeeming
        // slid the deadline forward, a busy lobby's code would effectively never expire.
        var credential = Issue(Now);
        var originalDeadline = credential.ExpiresAtUtc;

        _clock.Advance(TimeSpan.FromMinutes(20));
        credential.CanRedeem(Now).Should().BeTrue();

        credential.ExpiresAtUtc.Should().Be(originalDeadline);

        _clock.Advance(TimeSpan.FromMinutes(10));
        credential.CanRedeem(Now).Should().BeFalse("the original 30-minute deadline has now passed");
    }

    // ── Rotation ─────────────────────────────────────────────────────────────

    [Fact]
    public void RotationKillsTheOldCredentialImmediately()
    {
        var credential = Issue(Now);
        credential.CanRedeem(Now).Should().BeTrue();

        credential.MarkRotated(Now);

        credential.State.Should().Be(LobbyCredentialState.Rotated);
        credential.CanRedeem(Now).Should().BeFalse("the old value is dead the instant it is replaced");
        credential.SupersededAtUtc.Should().Be(Now);
    }

    [Fact]
    public void RevocationKillsTheCredentialImmediately()
    {
        var credential = Issue(Now);

        credential.Revoke(Now);

        credential.State.Should().Be(LobbyCredentialState.Revoked);
        credential.CanRedeem(Now).Should().BeFalse();
    }

    [Fact]
    public void RotationAndRevocationAreIdempotent()
    {
        var credential = Issue(Now);
        credential.MarkRotated(Now);
        var supersededAt = credential.SupersededAtUtc;

        _clock.Advance(TimeSpan.FromMinutes(1));
        credential.MarkRotated(Now);
        credential.Revoke(Now);

        credential.State.Should().Be(LobbyCredentialState.Rotated, "the first terminal transition wins");
        credential.SupersededAtUtc.Should().Be(supersededAt);
    }

    [Fact]
    public void ACredentialCannotBeIssuedWithoutDigests()
    {
        var act = () => LobbyJoinCredential.Issue(Guid.NewGuid(), "", "link", 1, Now);

        act.Should().Throw<ArgumentException>();
    }
}

/// <summary>Plaintext generation: entropy, alphabet safety, and input normalization.</summary>
public class LobbyCredentialFormatTests
{
    [Fact]
    public void TheManualCodeCarriesAtLeastSixtyBitsOfEntropy()
    {
        // The brief's floor. 12 symbols from a 32-symbol alphabet is exactly 5 bits each.
        LobbyCredentialFormat.CodeEntropyBits.Should().BeGreaterThanOrEqualTo(60);
    }

    [Fact]
    public void TheAlphabetExcludesTheCharactersPeopleMisreadForEachOther()
    {
        LobbyCredentialFormat.Alphabet.Should().NotContain("0");
        LobbyCredentialFormat.Alphabet.Should().NotContain("O");
        LobbyCredentialFormat.Alphabet.Should().NotContain("1");
        LobbyCredentialFormat.Alphabet.Should().NotContain("I");
    }

    [Fact]
    public void TheAlphabetIsExactlyThirtyTwoSymbols()
    {
        // Not cosmetic: 256 is an exact multiple of 32, which is what makes the `byte % 32` mapping in NewCode()
        // uniform. A 31- or 33-symbol alphabet would silently introduce modulo bias and cost real entropy.
        LobbyCredentialFormat.Alphabet.Should().HaveLength(32);
        LobbyCredentialFormat.Alphabet.Distinct().Should().HaveCount(32, "a repeated symbol would also bias the draw");
    }

    [Fact]
    public void AGeneratedCodeUsesOnlyAlphabetSymbolsAndGroupSeparators()
    {
        var code = LobbyCredentialFormat.NewCode();

        code.Should().MatchRegex("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$");
        code.Replace("-", "").ToCharArray().Should()
            .OnlyContain(c => LobbyCredentialFormat.Alphabet.Contains(c));
    }

    [Fact]
    public void GeneratedCodesDoNotRepeat()
    {
        // A weak smoke test for the RNG: 500 draws from a 60-bit space must not collide. A constant or
        // low-entropy generator would fail this instantly.
        var codes = Enumerable.Range(0, 500).Select(_ => LobbyCredentialFormat.NewCode()).ToList();

        codes.Distinct().Should().HaveCount(codes.Count);
    }

    [Fact]
    public void GeneratedLinkTokensAreDistinctAndUrlSafe()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => LobbyCredentialFormat.NewLinkToken()).ToList();

        tokens.Distinct().Should().HaveCount(tokens.Count);
        tokens.Should().OnlyContain(t => t.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));
    }

    [Theory]
    [InlineData("K7M2-9QRB-XTFH")]
    [InlineData("k7m2-9qrb-xtfh")]
    [InlineData("K7M29QRBXTFH")]
    [InlineData("k7m2 9qrb xtfh")]
    [InlineData(" K7M2-9QRB-XTFH ")]
    public void NormalizationMakesCasingAndSeparatorsIrrelevant(string typed)
    {
        // A user reading a code off a screen will type it however they like. All of these must hash to the same
        // digest as the issued value, or the code appears broken for no reason the user can see.
        LobbyCredentialFormat.NormalizeCode(typed).Should().Be("K7M29QRBXTFH");
    }
}

/// <summary>The keyed-digest boundary: keyed, deterministic, and constant-time.</summary>
public class HmacLobbyCredentialHasherTests
{
    private static HmacLobbyCredentialHasher Hasher(string key = "test-key-at-least-32-characters-long!!") =>
        new(Options.Create(new LobbyCredentialOptions { Key = key }));

    [Fact]
    public void HashingIsDeterministicForTheSameKeyAndInput()
    {
        var hasher = Hasher();

        hasher.HashCode("K7M2-9QRB-XTFH").Should().Be(hasher.HashCode("K7M2-9QRB-XTFH"));
    }

    [Fact]
    public void TheDigestDependsOnTheServerKey()
    {
        // This is the whole point of keying: with a bare hash, anyone holding the database could exhaust a 60-bit
        // code offline. Two different keys must produce different digests for the same code.
        var a = Hasher("key-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var b = Hasher("key-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        a.HashCode("K7M2-9QRB-XTFH").Should().NotBe(b.HashCode("K7M2-9QRB-XTFH"));
    }

    [Fact]
    public void TheDigestNeverContainsThePlaintext()
    {
        var hasher = Hasher();
        const string code = "K7M2-9QRB-XTFH";

        var digest = hasher.HashCode(code);

        digest.Should().NotContain("K7M2");
        digest.Should().MatchRegex("^[0-9a-f]{64}$", "a hex-encoded SHA-256 HMAC");
    }

    [Fact]
    public void EquivalentlyTypedCodesProduceTheSameDigest()
    {
        var hasher = Hasher();

        hasher.HashCode("k7m2 9qrb xtfh").Should().Be(hasher.HashCode("K7M2-9QRB-XTFH"));
    }

    [Fact]
    public void ADifferentCodeProducesADifferentDigest()
    {
        var hasher = Hasher();

        hasher.HashCode("K7M2-9QRB-XTFH").Should().NotBe(hasher.HashCode("K7M2-9QRB-XTFJ"));
    }

    [Fact]
    public void CodeAndLinkTokenDigestsAreComputedOverDifferentInputs()
    {
        // The link token is not normalized (it is machine-generated and exact); the code is. Feeding the same
        // string through both must therefore not collide by construction.
        var hasher = Hasher();

        hasher.HashLinkToken("abc-def").Should().NotBe(hasher.HashCode("abc-def"));
    }

    [Fact]
    public void MatchingDigestsCompareEqual()
    {
        var hasher = Hasher();
        var digest = hasher.HashCode("K7M2-9QRB-XTFH");

        hasher.DigestsMatch(digest, hasher.HashCode("K7M2-9QRB-XTFH")).Should().BeTrue();
    }

    [Fact]
    public void NonMatchingDigestsCompareUnequal()
    {
        var hasher = Hasher();

        hasher.DigestsMatch(hasher.HashCode("K7M2-9QRB-XTFH"), hasher.HashCode("XXXX-XXXX-XXXX"))
            .Should().BeFalse();
    }

    [Fact]
    public void AMalformedDigestFailsClosedRatherThanThrowing()
    {
        // A truncated or non-hex stored value must be a clean "no match", not a 500 that tells an attacker they
        // found a row with a corrupt digest.
        var hasher = Hasher();
        var valid = hasher.HashCode("K7M2-9QRB-XTFH");

        hasher.DigestsMatch("not-hex", valid).Should().BeFalse();
        hasher.DigestsMatch(valid, "abcd").Should().BeFalse();
        hasher.DigestsMatch("", valid).Should().BeFalse();
    }

    [Fact]
    public void AnUnconfiguredKeyFailsClosedAtConstruction()
    {
        // No dev fallback: silently degrading to an unkeyed digest would make every code in the database
        // offline-guessable, and nothing would look wrong.
        var act = () => new HmacLobbyCredentialHasher(Options.Create(new LobbyCredentialOptions { Key = "" }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*LobbyCredential:Key*");
    }
}
