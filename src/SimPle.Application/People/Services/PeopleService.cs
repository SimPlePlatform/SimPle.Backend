using Microsoft.Extensions.Options;
using SimPle.Application.Common.Interfaces;
using SimPle.Application.Common.Options;
using SimPle.Application.Common.Pagination;
using SimPle.Application.People.DTOs;
using SimPle.Shared.Common;

namespace SimPle.Application.People.Services;

public sealed class PeopleService : IPeopleService
{
    private readonly IFriendRepository _friends;
    private readonly IFileStorageService _storage;
    private readonly StorageOptions _storageOptions;

    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;

    // Bump when SearchPeopleAsync's ranking/bucket rules change; an in-flight cursor from a prior
    // algorithm version is rejected rather than silently reinterpreted against the new ranking.
    private const int RankingVersion = 1;

    private const string ValidationFailed = "Validation.Failed";
    private const string InvalidCursor = "Pagination.InvalidCursor";

    public PeopleService(IFriendRepository friends, IFileStorageService storage, IOptions<StorageOptions> storageOptions)
    {
        _friends = friends;
        _storage = storage;
        _storageOptions = storageOptions.Value;
    }

    public async Task<Result<CursorPage<PeopleSearchResultDto>>> SearchAsync(
        Guid actorId, string query, int limit, string? cursor, CancellationToken ct = default)
    {
        var normalized = NormalizeQuery(query, out var queryError);
        if (queryError is not null)
            return Result<CursorPage<PeopleSearchResultDto>>.Fail(ValidationFailed, queryError);

        limit = ClampLimit(limit);

        int? afterBucket = null;
        string? afterSortKey = null;
        Guid? afterId = null;
        if (cursor is not null)
        {
            if (!Cursor.TryDecodePeopleSearch(
                    cursor, out var bucket, out var sortKey, out var id, out var cursorQuery, out var rankingVersion)
                || cursorQuery != normalized || rankingVersion != RankingVersion)
            {
                return Result<CursorPage<PeopleSearchResultDto>>.Fail(InvalidCursor, "The pagination cursor is invalid.");
            }
            afterBucket = bucket;
            afterSortKey = sortKey;
            afterId = id;
        }

        var rows = await _friends.SearchPeopleAsync(actorId, normalized!, limit, afterBucket, afterSortKey, afterId, ct);

        var items = new List<PeopleSearchResultDto>(rows.Count);
        foreach (var (user, _, mutualCount, relationshipState) in rows)
        {
            var avatar = await BuildAvatarUrlAsync(user.AvatarObjectKey, user.AvatarUrl, ct);
            items.Add(new PeopleSearchResultDto(
                user.Id, user.Username, user.DisplayName, user.Initials, user.Color, avatar,
                user.ProfileType.ToString(), mutualCount, relationshipState));
        }

        string? next = rows.Count == limit
            ? Cursor.EncodePeopleSearch(rows[^1].bucket, rows[^1].user.NormalizedUsername, rows[^1].user.Id, normalized!, RankingVersion)
            : null;

        return Result<CursorPage<PeopleSearchResultDto>>.Ok(new CursorPage<PeopleSearchResultDto>(items, next));
    }

    private static int ClampLimit(int limit) => limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);

    /// <summary>Required 2–100 normalized chars (leading @ stripped, upper-invariant); blank is an error.</summary>
    private static string? NormalizeQuery(string query, out string? error)
    {
        error = null;
        query = query.Trim();
        if (query.StartsWith('@')) query = query[1..];
        if (query.Length < 2 || query.Length > 100)
        {
            error = "Search query must be between 2 and 100 characters.";
            return null;
        }
        return query.ToUpperInvariant();
    }

    private async Task<string?> BuildAvatarUrlAsync(string? objectKey, string? fallbackUrl, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(objectKey))
        {
            var expiry = TimeSpan.FromMinutes(_storageOptions.ReadUrlExpiryMinutes);
            return await _storage.CreatePresignedReadUrlAsync(objectKey, expiry, ct);
        }
        return fallbackUrl;
    }
}
