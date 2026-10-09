using Microsoft.EntityFrameworkCore;
using TradeOps.Application.Interfaces;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Repositories;

public sealed class EfTradingSignalOutcomeHistoryRepository(TradeOpsDbContext dbContext)
    : ITradingSignalOutcomeHistoryRepository
{
    public async Task<IReadOnlyCollection<TradingSignalOutcomeEvent>> GetBySignalIdAsync(
        Guid signalId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.TradingSignalOutcomeEvents
            .AsNoTracking()
            .Where(item => item.TradingSignalId == signalId)
            .OrderBy(item => item.OccurredAt)
            .ThenBy(item => item.Id)
            .ToArrayAsync(cancellationToken);
    }
}
