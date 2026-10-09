using Microsoft.EntityFrameworkCore;
using TradeOps.Application.Interfaces;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Repositories;

public sealed class EfOperationalRunHistoryRepository(TradeOpsDbContext dbContext)
    : IOperationalRunHistoryRepository
{
    public async Task<IReadOnlyCollection<OperationalRunRecord>> GetRecentAsync(
        string? runType,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);
        var query = dbContext.OperationalRuns.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(runType))
        {
            var normalizedRunType = runType.Trim();
            query = query.Where(item => item.RunType == normalizedRunType);
        }

        return await query
            .OrderByDescending(item => item.StartedAt)
            .ThenByDescending(item => item.UpdatedAt)
            .Take(safeLimit)
            .ToArrayAsync(cancellationToken);
    }
}
