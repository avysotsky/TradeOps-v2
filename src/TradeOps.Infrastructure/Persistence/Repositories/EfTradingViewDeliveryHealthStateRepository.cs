using Microsoft.EntityFrameworkCore;
using TradeOps.Application.Interfaces;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Repositories;

public sealed class EfTradingViewDeliveryHealthStateRepository(
    TradeOpsDbContext dbContext)
    : ITradingViewDeliveryHealthStateRepository
{
    public Task<TradingViewDeliveryHealthState?> GetAsync(
        CancellationToken cancellationToken = default)
    {
        return dbContext.TradingViewDeliveryHealthStates
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id
                    == TradingViewDeliveryHealthState.SingletonId,
                cancellationToken);
    }

    public async Task UpsertAsync(
        TradingViewDeliveryHealthState state,
        CancellationToken cancellationToken = default)
    {
        var existing = await dbContext
            .TradingViewDeliveryHealthStates
            .SingleOrDefaultAsync(
                item => item.Id == state.Id,
                cancellationToken);

        if (existing is null)
        {
            dbContext.TradingViewDeliveryHealthStates.Add(state);
        }
        else
        {
            existing.Status = state.Status;
            existing.Reason = state.Reason;
            existing.UpdatedAt = state.UpdatedAt;
            existing.WindowFrom = state.WindowFrom;
            existing.WindowTo = state.WindowTo;
            existing.Total = state.Total;
            existing.Failed = state.Failed;
            existing.Conflict = state.Conflict;
            existing.RiskRejected = state.RiskRejected;
            existing.AverageLatencyMilliseconds =
                state.AverageLatencyMilliseconds;
            existing.LatestDeliveryAt = state.LatestDeliveryAt;
            existing.LatestSuccessfulAt =
                state.LatestSuccessfulAt;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
