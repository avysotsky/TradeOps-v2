using TradeOps.Domain.Enums;

namespace TradeOps.Domain.Entities;

public sealed class Position
{
    public required string Symbol { get; init; }

    public OrderSide Side { get; init; }

    public decimal Quantity { get; init; }

    public decimal AverageEntryPrice { get; init; }

    public decimal MarkPrice { get; init; }

    public decimal UnrealizedPnL { get; init; }
}
