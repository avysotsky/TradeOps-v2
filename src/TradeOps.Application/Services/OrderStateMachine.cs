using TradeOps.Application.Interfaces;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Services;

public sealed class OrderStateMachine : IOrderStateMachine
{
    private static readonly IReadOnlyDictionary<OrderStatus, HashSet<OrderStatus>> AllowedTransitions =
        new Dictionary<OrderStatus, HashSet<OrderStatus>>
        {
            [OrderStatus.Created] =
            [
                OrderStatus.Submitted,
                OrderStatus.Cancelled
            ],
            [OrderStatus.Submitted] =
            [
                OrderStatus.Accepted,
                OrderStatus.PartiallyFilled,
                OrderStatus.Filled,
                OrderStatus.Rejected,
                OrderStatus.Cancelled,
                OrderStatus.Unknown
            ],
            [OrderStatus.Accepted] =
            [
                OrderStatus.PartiallyFilled,
                OrderStatus.Filled,
                OrderStatus.Cancelled,
                OrderStatus.Unknown
            ],
            [OrderStatus.PartiallyFilled] =
            [
                OrderStatus.Filled,
                OrderStatus.Cancelled,
                OrderStatus.Unknown
            ],
            [OrderStatus.Unknown] =
            [
                OrderStatus.Submitted,
                OrderStatus.Accepted,
                OrderStatus.PartiallyFilled,
                OrderStatus.Filled,
                OrderStatus.Cancelled,
                OrderStatus.Rejected
            ],
            [OrderStatus.Filled] = [],
            [OrderStatus.Cancelled] = [],
            [OrderStatus.Rejected] = []
        };

    public bool CanTransition(OrderStatus currentStatus, OrderStatus nextStatus)
    {
        if (currentStatus == nextStatus)
        {
            return true;
        }

        return AllowedTransitions.TryGetValue(currentStatus, out var allowed)
            && allowed.Contains(nextStatus);
    }

    public void Apply(
        Order order,
        OrderStatus nextStatus,
        decimal filledQuantity,
        decimal? averageFillPrice,
        string? exchangeOrderId = null,
        decimal? price = null)
    {
        if (!CanTransition(order.Status, nextStatus))
        {
            throw new InvalidOperationException(
                $"Invalid order state transition: {order.Status} -> {nextStatus} for {order.ClientOrderId}.");
        }

        if (filledQuantity < 0m || filledQuantity > order.RequestedQuantity)
        {
            throw new InvalidOperationException(
                $"Filled quantity {filledQuantity} is outside 0..{order.RequestedQuantity} for {order.ClientOrderId}.");
        }

        if (nextStatus == OrderStatus.PartiallyFilled
            && (filledQuantity <= 0m || filledQuantity >= order.RequestedQuantity))
        {
            throw new InvalidOperationException(
                $"PartiallyFilled requires filled quantity between 0 and requested quantity for {order.ClientOrderId}.");
        }

        if (nextStatus == OrderStatus.Filled
            && filledQuantity != order.RequestedQuantity)
        {
            throw new InvalidOperationException(
                $"Filled order must have FilledQuantity equal to RequestedQuantity for {order.ClientOrderId}.");
        }

        order.Status = nextStatus;
        order.FilledQuantity = filledQuantity;
        order.AverageFillPrice = averageFillPrice;

        if (!string.IsNullOrWhiteSpace(exchangeOrderId))
        {
            order.ExchangeOrderId = exchangeOrderId;
        }

        if (price is not null)
        {
            order.Price = price;
        }

        order.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
