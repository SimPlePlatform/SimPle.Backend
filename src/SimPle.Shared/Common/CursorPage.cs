namespace SimPle.Shared.Common;

/// <summary>
/// A keyset-paginated slice. <see cref="NextCursor"/> is an opaque base64url token (last normalized sort
/// key + UUID tie-breaker) or <c>null</c> when the last page has been reached. No total/count is promised;
/// pages are best-effort with no snapshot isolation, so consumers dedupe by id.
/// </summary>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);
