using SimPle.Domain.Profiles;

namespace SimPle.Application.Common.Interfaces;

public interface IRetiredUsernameRepository
{
    Task<bool> IsRetiredAsync(string normalizedUsername, CancellationToken ct = default);
    Task AddAsync(RetiredUsername retired, CancellationToken ct = default);
}
