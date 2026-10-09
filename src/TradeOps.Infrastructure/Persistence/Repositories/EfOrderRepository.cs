using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Application.Interfaces;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Persistence.Repositories;

public sealed class EfOrderRepository(TradeOpsDbContext dbContext) : IOrderRepository
{
    public Task<Order?> GetByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(
                order => order.ClientOrderId == clientOrderId,
                cancellationToken);
    }

    public Task<Order?> GetByExchangeOrderIdAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(
                order => order.ExchangeOrderId == exchangeOrderId,
                cancellationToken);
    }

    public async Task<IReadOnlyCollection<Order>> GetReconciliationCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        return await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.Status == OrderStatus.Submitted
                || order.Status == OrderStatus.Accepted
                || order.Status == OrderStatus.PartiallyFilled
                || order.Status == OrderStatus.Unknown)
            .OrderBy(order => order.CreatedAt)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<bool> TryAddAsync(
        Order order,
        CancellationToken cancellationToken = default)
    {
        dbContext.Orders.Add(order);
        dbContext.OrderLifecycleEvents.Add(CreateLifecycleEvent(order, null, "Created"));

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task UpdateAsync(
        Order order,
        CancellationToken cancellationToken = default)
    {
        var previous = await dbContext.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == order.Id, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Cannot update local order '{order.ClientOrderId}' because it no longer exists.");

        var shouldAudit = previous.Status != order.Status
            || previous.FilledQuantity != order.FilledQuantity
            || previous.AverageFillPrice != order.AverageFillPrice
            || previous.ExchangeOrderId != order.ExchangeOrderId;

        dbContext.Orders.Update(order);

        if (shouldAudit)
        {
            dbContext.OrderLifecycleEvents.Add(
                CreateLifecycleEvent(order, previous.Status, "PersistenceUpdate"));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static OrderLifecycleEvent CreateLifecycleEvent(
        Order order,
        OrderStatus? previousStatus,
        string source)
    {
        return new OrderLifecycleEvent
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            ClientOrderId = order.ClientOrderId,
            PreviousStatus = previousStatus,
            Status = order.Status,
            FilledQuantity = order.FilledQuantity,
            AverageFillPrice = order.AverageFillPrice,
            ExchangeOrderId = order.ExchangeOrderId,
            Source = source,
            OccurredAt = DateTimeOffset.UtcNow
        };
    }
}
