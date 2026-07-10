using SimPle.Application.People.DTOs;
using SimPle.Shared.Common;

namespace SimPle.Application.People.Services;

public interface IPeopleService
{
    Task<Result<CursorPage<PeopleSearchResultDto>>> SearchAsync(
        Guid actorId, string query, int limit, string? cursor, CancellationToken ct = default);
}
