using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Repositories;

public sealed class EfFillRepository(TradeOpsDbContext dbContext) : IFillRepository
{
    public async Task<bool> TryAddAsync(
        Fill fill,
        CancellationToken cancellationToken = default)
    {
        dbContext.Fills.Add(fill);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            dbContext.Entry(fill).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<IReadOnlyCollection<PositionFill>> GetPositionFillsAsync(
        string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        var query =
            from fill in dbContext.Fills.AsNoTracking()
            join order in dbContext.Orders.AsNoTracking()
                on fill.OrderId equals order.Id
            select new { Fill = fill, Order = order };

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            query = query.Where(item => item.Order.Symbol == symbol);
        }

        return await query
            .OrderBy(item => item.Fill.FilledAt)
            .ThenBy(item => item.Fill.ExchangeFillId)
            .Select(item => new PositionFill(
                item.Order.Symbol,
                item.Order.Side,
                item.Fill.Quantity,
                item.Fill.Price,
                item.Fill.FilledAt,
                item.Fill.ExchangeFillId,
                item.Fill.Fee,
                item.Fill.FeeCurrency))
            .ToArrayAsync(cancellationToken);
    }
}
