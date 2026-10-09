using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Interfaces;

public interface IOrderStateMachine
{
    bool CanTransition(OrderStatus currentStatus, OrderStatus nextStatus);

    void Apply(
        Order order,
        OrderStatus nextStatus,
        decimal filledQuantity,
        decimal? averageFillPrice,
        string? exchangeOrderId = null,
        decimal? price = null);
}
