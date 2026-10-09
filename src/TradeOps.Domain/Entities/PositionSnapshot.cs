using TradeOps.Domain.Enums;

namespace TradeOps.Domain.Entities;

public sealed class PositionSnapshot
{
    public Guid Id { get; set; }

    public required string Symbol { get; set; }

    public OrderSide? Side { get; set; }

    public decimal Quantity { get; set; }

    public decimal AverageEntryPrice { get; set; }

    public decimal RealizedPnL { get; set; }

    public decimal? MarkPrice { get; set; }

    public decimal? UnrealizedPnL { get; set; }

    public DateTimeOffset CapturedAt { get; set; }
}
