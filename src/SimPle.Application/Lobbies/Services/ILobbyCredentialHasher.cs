namespace SimPle.Application.Lobbies.Services;

/// <summary>
/// Turns a plaintext join credential into the keyed digest that is the only form ever persisted, and compares a
/// user-supplied attempt against a stored digest in constant time.
///
/// Keyed (HMAC), not a bare hash: a join code carries only ~60 bits, so an attacker who obtained the database could
/// exhaust an unkeyed digest offline. The server key makes the stored digests useless without it.
///
/// Constant-time comparison closes the timing side channel — a byte-by-byte compare would leak how much of a
/// guessed code was correct, turning a 60-bit search into a per-character one.
/// </summary>
public interface ILobbyCredentialHasher
{
    /// <summary>Digest of a manual join code. Normalizes the input first, so casing and separators do not matter.</summary>
    string HashCode(string plaintextCode);

    /// <summary>Digest of a 128-bit share-link token. Not normalized — the token is machine-generated and exact.</summary>
    string HashLinkToken(string plaintextLinkToken);

    /// <summary>
    /// Constant-time equality of two digests. Always compare through this — never with <c>==</c>, which
    /// short-circuits on the first differing byte.
    /// </summary>
    bool DigestsMatch(string storedDigest, string candidateDigest);
}
