using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SimPle.Application.Common.Options;
using SimPle.Application.Lobbies.Services;
using SimPle.Domain.Lobbies;

namespace SimPle.Infrastructure.Lobbies;

/// <summary>
/// HMAC-SHA256 keyed digests for lobby join credentials, compared with
/// <see cref="CryptographicOperations.FixedTimeEquals"/>.
///
/// The key is held here and never leaves: the domain entity stores only digests, so no plaintext credential exists
/// anywhere it could reach a log line, an outbox payload, or a DTO (Risk #7).
/// </summary>
public sealed class HmacLobbyCredentialHasher : ILobbyCredentialHasher
{
    private readonly byte[] _key;

    public HmacLobbyCredentialHasher(IOptions<LobbyCredentialOptions> options)
    {
        var key = options.Value.Key;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "LobbyCredential:Key is not configured. Set it outside committed appsettings " +
                "(environment variable LobbyCredential__Key).");
        }

        _key = Encoding.UTF8.GetBytes(key);
    }

    public string HashCode(string plaintextCode)
    {
        ArgumentNullException.ThrowIfNull(plaintextCode);
        return Digest(LobbyCredentialFormat.NormalizeCode(plaintextCode));
    }

    public string HashLinkToken(string plaintextLinkToken)
    {
        ArgumentNullException.ThrowIfNull(plaintextLinkToken);
        return Digest(plaintextLinkToken);
    }

    public bool DigestsMatch(string storedDigest, string candidateDigest)
    {
        if (storedDigest is null || candidateDigest is null) return false;

        // Compare the decoded bytes, not the strings: FixedTimeEquals needs equal-length spans, and two valid
        // digests are always the same length. A malformed stored value fails closed rather than throwing.
        if (!TryDecode(storedDigest, out var stored) || !TryDecode(candidateDigest, out var candidate))
            return false;

        return CryptographicOperations.FixedTimeEquals(stored, candidate);
    }

    private string Digest(string plaintext)
    {
        var mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(plaintext));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    private static bool TryDecode(string digest, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(digest);
            return bytes.Length == SHA256.HashSizeInBytes;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }
}
