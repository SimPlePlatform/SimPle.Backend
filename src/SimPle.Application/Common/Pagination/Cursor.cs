using System.Text;

namespace SimPle.Application.Common.Pagination;

/// <summary>
/// Opaque base64url keyset cursors: the last row's normalized sort key plus its UUID tie-breaker. Decoding
/// is total — any malformed / forged / truncated token returns <c>false</c> so the caller can answer 400
/// <c>Pagination.InvalidCursor</c> rather than trusting attacker-supplied offsets. No dependency on ASP.NET;
/// base64url is implemented locally to keep the Application layer framework-free.
/// </summary>
public static class Cursor
{
    private const char Sep = ':';

    // ── (DateTime, Guid) cursors: requests (SentAt DESC, Id DESC), blocks (CreatedAt DESC, Id DESC) ──

    public static string EncodeTimeId(DateTime time, Guid id) =>
        ToBase64Url($"{time.Ticks}{Sep}{id:N}");

    public static bool TryDecodeTimeId(string? cursor, out DateTime time, out Guid id)
    {
        time = default;
        id = default;
        if (!TryFromBase64Url(cursor, out var raw)) return false;
        var idx = raw.IndexOf(Sep);
        if (idx <= 0 || idx == raw.Length - 1) return false;
        if (!long.TryParse(raw[..idx], out var ticks)) return false;
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return false;
        if (!Guid.TryParseExact(raw[(idx + 1)..], "N", out id)) return false;
        time = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    // ── (string, Guid) cursors: friends (normalizedDisplayName, userId) ──
    // The string key is itself base64url-encoded so it can never collide with the separator.

    public static string EncodeStringId(string key, Guid id) =>
        ToBase64Url($"{ToBase64Url(key)}{Sep}{id:N}");

    public static bool TryDecodeStringId(string? cursor, out string key, out Guid id)
    {
        key = string.Empty;
        id = default;
        if (!TryFromBase64Url(cursor, out var raw)) return false;
        var idx = raw.IndexOf(Sep);
        if (idx < 0 || idx == raw.Length - 1) return false;
        if (!TryFromBase64Url(raw[..idx], out key)) return false;
        if (!Guid.TryParseExact(raw[(idx + 1)..], "N", out id)) return false;
        return true;
    }

    // ── People-search cursor: (bucket, normalizedSortKey, Guid) position, bound to (normalizedQuery,
    // rankingVersion) so a cursor cannot be replayed against a different query or after a ranking-algorithm
    // change. Segment components are individually base64url-encoded so a plain Split on Sep is safe (no
    // component can ever contain the raw separator).

    public static string EncodePeopleSearch(int bucket, string sortKey, Guid id, string normalizedQuery, int rankingVersion) =>
        ToBase64Url($"{bucket}{Sep}{ToBase64Url(sortKey)}{Sep}{id:N}{Sep}{ToBase64Url(normalizedQuery)}{Sep}{rankingVersion}");

    public static bool TryDecodePeopleSearch(
        string? cursor, out int bucket, out string sortKey, out Guid id, out string normalizedQuery, out int rankingVersion)
    {
        bucket = default;
        sortKey = string.Empty;
        id = default;
        normalizedQuery = string.Empty;
        rankingVersion = default;
        if (!TryFromBase64Url(cursor, out var raw)) return false;
        var parts = raw.Split(Sep);
        if (parts.Length != 5) return false;
        if (!int.TryParse(parts[0], out bucket)) return false;
        if (!TryFromBase64Url(parts[1], out sortKey)) return false;
        if (!Guid.TryParseExact(parts[2], "N", out id)) return false;
        if (!TryFromBase64Url(parts[3], out normalizedQuery)) return false;
        if (!int.TryParse(parts[4], out rankingVersion)) return false;
        return true;
    }

    // ── Profile friend/mutual-friend list cursor: (normalizedSortKey, Guid) position, bound to
    // (targetUserId, normalizedFilter, policyVersion, listContext) so a cursor cannot be replayed against a
    // different target, search filter, listContext ("friends" vs "mutual"), or after the target's privacy
    // policy version changes mid-pagination.

    public static string EncodeProfileList(
        string sortKey, Guid id, Guid targetUserId, string normalizedFilter, long policyVersion, string listContext) =>
        ToBase64Url($"{ToBase64Url(sortKey)}{Sep}{id:N}{Sep}{targetUserId:N}{Sep}{ToBase64Url(normalizedFilter)}{Sep}{policyVersion}{Sep}{ToBase64Url(listContext)}");

    public static bool TryDecodeProfileList(
        string? cursor, out string sortKey, out Guid id, out Guid targetUserId, out string normalizedFilter,
        out long policyVersion, out string listContext)
    {
        sortKey = string.Empty;
        id = default;
        targetUserId = default;
        normalizedFilter = string.Empty;
        policyVersion = default;
        listContext = string.Empty;
        if (!TryFromBase64Url(cursor, out var raw)) return false;
        var parts = raw.Split(Sep);
        if (parts.Length != 6) return false;
        if (!TryFromBase64Url(parts[0], out sortKey)) return false;
        if (!Guid.TryParseExact(parts[1], "N", out id)) return false;
        if (!Guid.TryParseExact(parts[2], "N", out targetUserId)) return false;
        if (!TryFromBase64Url(parts[3], out normalizedFilter)) return false;
        if (!long.TryParse(parts[4], out policyVersion)) return false;
        if (!TryFromBase64Url(parts[5], out listContext)) return false;
        return true;
    }

    // ── Game catalog cursor: (sortKey, Slug) position, bound to a hash of the normalized query shape
    // (search text, category/tag/mode/lifecycle filters, sort) so a cursor cannot be replayed after the
    // filter/sort shape changes — it must fail with Pagination.InvalidCursor rather than blend result sets.
    // sortKey is a caller-formatted, order-preserving string representation of the active sort's key(s)
    // (e.g. the default order's zero-padded FeaturedRank/SortOrder composite, or the uppercased Name).

    public static string EncodeCatalog(string sortKey, string slug, string queryShapeHash) =>
        ToBase64Url($"{ToBase64Url(sortKey)}{Sep}{ToBase64Url(slug)}{Sep}{ToBase64Url(queryShapeHash)}");

    public static bool TryDecodeCatalog(string? cursor, out string sortKey, out string slug, out string queryShapeHash)
    {
        sortKey = string.Empty;
        slug = string.Empty;
        queryShapeHash = string.Empty;
        if (!TryFromBase64Url(cursor, out var raw)) return false;
        var parts = raw.Split(Sep);
        if (parts.Length != 3) return false;
        if (!TryFromBase64Url(parts[0], out sortKey)) return false;
        if (!TryFromBase64Url(parts[1], out slug)) return false;
        if (!TryFromBase64Url(parts[2], out queryShapeHash)) return false;
        return true;
    }

    // ── base64url (RFC 4648 §5) without external dependencies ──

    private static string ToBase64Url(string value)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryFromBase64Url(string? value, out string result)
    {
        result = string.Empty;
        if (value is null) return false;

        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: return false; // never a valid base64 length
        }

        try
        {
            result = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
