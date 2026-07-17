using Microsoft.EntityFrameworkCore;
using SimPle.Application.Outbox;
using SimPle.Domain.Outbox;
using SimPle.Infrastructure.Persistence;

namespace SimPle.Infrastructure.Outbox;

/// <summary>See <see cref="IOutboxActivationStore"/>. Idempotent try-insert/catch-23505/re-read, matching this
/// codebase's established idempotent-insert idiom (e.g. lobby join-credential code generation).</summary>
public sealed class OutboxActivationStore : IOutboxActivationStore
{
    private readonly AppDbContext _db;

    public OutboxActivationStore(AppDbContext db) => _db = db;

    public async Task<OutboxHandlerActivation> GetOrActivateAsync(
        string handlerName,
        IReadOnlyList<string> eventTypes,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        var existing = await _db.OutboxHandlerActivations
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.HandlerName == handlerName, ct);
        if (existing is not null) return existing;

        // MAX(OccurredAtUtc, Id) over this handler's own event types, taken at the activation instant. The outbox
        // has no global sequence, so (OccurredAtUtc, Id) is the only ordering key available — the same one the
        // handler itself compares every later message against.
        var latest = await _db.OutboxMessages
            .AsNoTracking()
            .Where(m => eventTypes.Contains(m.EventType))
            .OrderByDescending(m => m.OccurredAtUtc)
            .ThenByDescending(m => m.Id)
            .Select(m => new { m.OccurredAtUtc, m.Id })
            .FirstOrDefaultAsync(ct);

        var watermarkOccurredAtUtc = latest?.OccurredAtUtc ?? DateTime.MinValue;
        var watermarkEventId = latest?.Id;

        var activation = OutboxHandlerActivation.Activate(handlerName, nowUtc, watermarkOccurredAtUtc, watermarkEventId);

        _db.OutboxHandlerActivations.Add(activation);

        try
        {
            await _db.SaveChangesAsync(ct);
            return activation;
        }
        catch (DbUpdateException ex) when (PostgresContention.IsContention(ex))
        {
            // Lost the race for this HandlerName's primary key: another instance activated first. Detach the
            // failed insert and re-read the winner's row rather than retrying our own watermark computation —
            // the whole point of the watermark is that it is captured exactly once, at first activation.
            _db.Entry(activation).State = EntityState.Detached;

            return await _db.OutboxHandlerActivations
                .AsNoTracking()
                .FirstAsync(a => a.HandlerName == handlerName, ct);
        }
    }
}
