using TradeOps.Domain.Enums;

namespace TradeOps.Domain.Entities;

public sealed class Order
{
    public Guid Id { get; set; }

    public string? ExchangeOrderId { get; set; }

    public required string ClientOrderId { get; set; }

    public required string Symbol { get; set; }

    public OrderSide Side { get; set; }

    public OrderType OrderType { get; set; }

    public decimal RequestedQuantity { get; set; }

    public decimal FilledQuantity { get; set; }

    public decimal? AverageFillPrice { get; set; }

    public decimal? Price { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Created;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
