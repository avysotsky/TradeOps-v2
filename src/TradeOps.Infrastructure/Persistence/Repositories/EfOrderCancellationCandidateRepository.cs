using Microsoft.EntityFrameworkCore;
using TradeOps.Application.Interfaces;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Persistence.Repositories;

public sealed class EfOrderCancellationCandidateRepository(TradeOpsDbContext dbContext)
    : IOrderCancellationCandidateRepository
{
    public async Task<IReadOnlyCollection<Order>> GetCancellationCandidatesAsync(
        string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Orders
            .AsNoTracking()
            .Where(order => order.Status == OrderStatus.Created
                || order.Status == OrderStatus.Submitted
                || order.Status == OrderStatus.Accepted
                || order.Status == OrderStatus.PartiallyFilled
                || order.Status == OrderStatus.Unknown);

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            var normalizedSymbol = symbol.Trim().ToUpperInvariant();
            query = query.Where(order => order.Symbol == normalizedSymbol);
        }

        return await query
            .OrderBy(order => order.CreatedAt)
            .ThenBy(order => order.Id)
            .ToArrayAsync(cancellationToken);
    }
}
