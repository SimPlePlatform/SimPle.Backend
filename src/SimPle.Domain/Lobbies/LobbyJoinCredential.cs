using System.Security.Cryptography;
using SimPle.Domain.Common;

namespace SimPle.Domain.Lobbies;

/// <summary>
/// The join code and link token for one lobby, stored <em>only</em> as keyed digests.
///
/// The entity never sees or holds a plaintext credential: <see cref="Issue"/> takes digests that the caller has
/// already computed with the server key (<c>ILobbyCredentialHasher</c>). That is what makes "never logged, never in
/// events, never a resource identifier" (Risk #7) a structural property rather than a coding convention — there is
/// no plaintext field on the aggregate that could leak into a DTO, a log line, or an outbox payload.
///
/// Rotation supersedes rather than mutates: the old row moves to <see cref="LobbyCredentialState.Rotated"/> and a
/// new row is issued at the next <see cref="Generation"/>, so the old value is dead the instant it is replaced.
/// </summary>
public class LobbyJoinCredential : Entity
{
    /// <summary>A private join credential expires 30 minutes after issue, or when the lobby closes/starts.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public Guid LobbyId { get; private set; }

    /// <summary>Keyed HMAC digest of the human-typed code. Never the plaintext.</summary>
    public string CodeDigest { get; private set; } = default!;

    /// <summary>Keyed HMAC digest of the 128-bit share-link token. A separate secret from the code.</summary>
    public string LinkTokenDigest { get; private set; } = default!;

    /// <summary>Bumped on every rotation. Generation 1 is the credential minted at lobby creation.</summary>
    public int Generation { get; private set; }

    public LobbyCredentialState State { get; private set; } = LobbyCredentialState.Active;
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? SupersededAtUtc { get; private set; }

    private LobbyJoinCredential() { }

    public static LobbyJoinCredential Issue(
        Guid lobbyId,
        string codeDigest,
        string linkTokenDigest,
        int generation,
        DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(codeDigest))
            throw new ArgumentException("CodeDigest must not be empty.", nameof(codeDigest));
        if (string.IsNullOrWhiteSpace(linkTokenDigest))
            throw new ArgumentException("LinkTokenDigest must not be empty.", nameof(linkTokenDigest));
        if (generation < 1)
            throw new ArgumentException("Generation must be at least 1.", nameof(generation));

        return new LobbyJoinCredential
        {
            LobbyId = lobbyId,
            CodeDigest = codeDigest,
            LinkTokenDigest = linkTokenDigest,
            Generation = generation,
            State = LobbyCredentialState.Active,
            ExpiresAtUtc = nowUtc + Lifetime,
        };
    }

    public bool IsActive => State == LobbyCredentialState.Active;

    public bool IsExpired(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;

    /// <summary>
    /// Redeemable only while active and unexpired. Redeeming does <em>not</em> extend
    /// <see cref="ExpiresAtUtc"/> — using a credential never refreshes its deadline.
    /// </summary>
    public bool CanRedeem(DateTime nowUtc) => IsActive && !IsExpired(nowUtc);

    /// <summary>Superseded by a newly issued generation. The old value dies immediately.</summary>
    public void MarkRotated(DateTime nowUtc)
    {
        if (!IsActive) return;   // idempotent

        State = LobbyCredentialState.Rotated;
        SupersededAtUtc = nowUtc;
        Touch();
    }

    /// <summary>Revoked outright with no successor (e.g. the lobby closed).</summary>
    public void Revoke(DateTime nowUtc)
    {
        if (!IsActive) return;   // idempotent

        State = LobbyCredentialState.Revoked;
        SupersededAtUtc = nowUtc;
        Touch();
    }
}

/// <summary>
/// Generates the plaintext credentials. Pure and key-free — turning a plaintext into a stored digest is the
/// separate concern of <c>ILobbyCredentialHasher</c>, which holds the server key.
/// </summary>
public static class LobbyCredentialFormat
{
    /// <summary>
    /// 32 symbols, deliberately excluding <c>0/O/1/I</c> so a code read aloud or off a screen cannot be mistyped.
    /// A 32-symbol alphabet is exactly 5 bits per character, and 256 is an exact multiple of 32, so the
    /// <c>byte % 32</c> mapping below is uniform — no modulo bias, no rejection sampling needed.
    /// </summary>
    public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>12 symbols x 5 bits = 60 bits, meeting the brief's "at least 60 bits" for the manual code.</summary>
    public const int CodeLength = 12;

    /// <summary>The link token is a separate 128-bit secret, per the brief.</summary>
    public const int LinkTokenBytes = 16;

    public static int CodeEntropyBits => CodeLength * 5;

    /// <summary>Generates a fresh manual join code, e.g. <c>K7M2-9QRB-XTFH</c>.</summary>
    public static string NewCode()
    {
        Span<byte> bytes = stackalloc byte[CodeLength];
        RandomNumberGenerator.Fill(bytes);

        Span<char> chars = stackalloc char[CodeLength + 2];   // two group separators
        var c = 0;
        for (var i = 0; i < CodeLength; i++)
        {
            if (i > 0 && i % 4 == 0)
                chars[c++] = '-';
            chars[c++] = Alphabet[bytes[i] % Alphabet.Length];
        }

        return new string(chars);
    }

    /// <summary>Generates a fresh 128-bit share-link token, URL-safe.</summary>
    public static string NewLinkToken()
    {
        Span<byte> bytes = stackalloc byte[LinkTokenBytes];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    /// <summary>
    /// Normalizes user-typed input before hashing: strips separators/whitespace and upper-cases, so
    /// <c>k7m2-9qrb-xtfh</c> and <c>K7M29QRBXTFH</c> hash to the same digest as the issued value.
    /// </summary>
    public static string NormalizeCode(string input)
    {
        Span<char> buffer = stackalloc char[CodeLength];
        var written = 0;

        foreach (var ch in input)
        {
            if (ch is '-' or ' ' or '\t') continue;
            if (written == CodeLength) return input.Trim().ToUpperInvariant();   // too long: let the compare fail

            buffer[written++] = char.ToUpperInvariant(ch);
        }

        return new string(buffer[..written]);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
