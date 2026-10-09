using Microsoft.EntityFrameworkCore;
using TradeOps.Application.Interfaces;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Persistence.Repositories;

public sealed class EfLocalOrderAuditRepository(TradeOpsDbContext dbContext)
    : ILocalOrderAuditRepository
{
    public async Task<IReadOnlyCollection<Order>> GetAsync(
        string? symbol,
        OrderStatus? status,
        DateTimeOffset? fromInclusive,
        DateTimeOffset? toExclusive,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 200);
        IQueryable<Order> query = dbContext.Orders.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var normalizedSymbol = symbol.Trim().ToUpperInvariant();
            query = query.Where(order => order.Symbol == normalizedSymbol);
        }

        if (status.HasValue)
        {
            query = query.Where(order => order.Status == status.Value);
        }

        if (fromInclusive.HasValue)
        {
            query = query.Where(order => order.CreatedAt >= fromInclusive.Value);
        }

        if (toExclusive.HasValue)
        {
            query = query.Where(order => order.CreatedAt < toExclusive.Value);
        }

        return await query
            .OrderByDescending(order => order.CreatedAt)
            .ThenByDescending(order => order.Id)
            .Take(safeLimit)
            .ToArrayAsync(cancellationToken);
    }
}
