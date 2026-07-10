using Microsoft.EntityFrameworkCore;
using SimPle.Application.Common.Interfaces;
using SimPle.Domain.Profiles;

namespace SimPle.Infrastructure.Persistence.Repositories;

public sealed class RetiredUsernameRepository : IRetiredUsernameRepository
{
    private readonly AppDbContext _db;

    public RetiredUsernameRepository(AppDbContext db) => _db = db;

    public Task<bool> IsRetiredAsync(string normalizedUsername, CancellationToken ct = default) =>
        _db.RetiredUsernames.AnyAsync(r => r.NormalizedUsername == normalizedUsername, ct);

    public async Task AddAsync(RetiredUsername retired, CancellationToken ct = default)
    {
        await _db.RetiredUsernames.AddAsync(retired, ct);
        await _db.SaveChangesAsync(ct);
    }
}
