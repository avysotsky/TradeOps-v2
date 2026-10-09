using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Application.Interfaces;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Repositories;

public sealed class EfRiskEventRepository(TradeOpsDbContext dbContext) : IRiskEventRepository
{
    public Task<RiskEvent?> GetActiveAsync(
        string eventType,
        string eventKey,
        CancellationToken cancellationToken = default)
    {
        return dbContext.RiskEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(
                riskEvent => riskEvent.EventType == eventType
                    && riskEvent.EventKey == eventKey
                    && riskEvent.ResolvedAt == null,
                cancellationToken);
    }

    public async Task<IReadOnlyCollection<RiskEvent>> GetActiveByTypeAsync(
        string eventType,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.RiskEvents
            .AsNoTracking()
            .Where(riskEvent => riskEvent.EventType == eventType
                && riskEvent.ResolvedAt == null)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<bool> TryAddAsync(
        RiskEvent riskEvent,
        CancellationToken cancellationToken = default)
    {
        dbContext.RiskEvents.Add(riskEvent);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            dbContext.Entry(riskEvent).State = EntityState.Detached;
            return false;
        }
    }

    public async Task UpdateAsync(
        RiskEvent riskEvent,
        CancellationToken cancellationToken = default)
    {
        dbContext.RiskEvents.Update(riskEvent);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<RiskEvent>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 500);

        return await dbContext.RiskEvents
            .AsNoTracking()
            .OrderByDescending(riskEvent => riskEvent.LastObservedAt)
            .Take(safeLimit)
            .ToArrayAsync(cancellationToken);
    }
}
