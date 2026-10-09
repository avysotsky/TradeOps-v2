using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Application.Interfaces;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Repositories;

public sealed class EfOperationalRiskStateRepository(TradeOpsDbContext dbContext)
    : IOperationalRiskStateRepository
{
    public async Task<OperationalRiskState> GetOrCreateAsync(
        bool initialTradingEnabled,
        bool initialEmergencyStop,
        CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.OperationalRiskStates
            .AsNoTracking()
            .SingleOrDefaultAsync(
                state => state.Id == OperationalRiskState.DefaultId,
                cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var created = new OperationalRiskState
        {
            Id = OperationalRiskState.DefaultId,
            TradingEnabled = initialTradingEnabled,
            EmergencyStop = initialEmergencyStop,
            EmergencyStopReason = initialEmergencyStop
                ? "Emergency stop initialized from RiskSettings."
                : null,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        dbContext.OperationalRiskStates.Add(created);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.Entry(created).State = EntityState.Detached;
            return created;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            dbContext.Entry(created).State = EntityState.Detached;

            return await dbContext.OperationalRiskStates
                .AsNoTracking()
                .SingleAsync(
                    state => state.Id == OperationalRiskState.DefaultId,
                    cancellationToken);
        }
    }

    public async Task UpdateAsync(
        OperationalRiskState state,
        CancellationToken cancellationToken = default)
    {
        dbContext.OperationalRiskStates.Update(state);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.Entry(state).State = EntityState.Detached;
    }
}
