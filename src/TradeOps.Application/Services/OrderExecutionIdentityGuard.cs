using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Services;

public static class OrderExecutionIdentityGuard
{
    public static void EnsureMatches(Order persistedOrder, TradingSignal signal)
    {
        var conflictingFields = new List<string>();

        if (!string.Equals(persistedOrder.Symbol, signal.Symbol, StringComparison.Ordinal))
        {
            conflictingFields.Add(nameof(Order.Symbol));
        }

        if (persistedOrder.Side != signal.Side)
        {
            conflictingFields.Add(nameof(Order.Side));
        }

        if (persistedOrder.OrderType != OrderType.Market)
        {
            conflictingFields.Add(nameof(Order.OrderType));
        }

        if (persistedOrder.RequestedQuantity != signal.RequestedQuantity)
        {
            conflictingFields.Add(nameof(Order.RequestedQuantity));
        }

        if (conflictingFields.Count > 0)
        {
            throw new ClientOrderIdConflictException(
                signal.Id,
                persistedOrder.ClientOrderId,
                persistedOrder.Id,
                conflictingFields);
        }
    }
}
