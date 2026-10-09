using TradeOps.Domain.Enums;

namespace TradeOps.Domain.Entities;

public sealed class OrderLifecycleEvent
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public required string ClientOrderId { get; set; }

    public OrderStatus? PreviousStatus { get; set; }

    public OrderStatus Status { get; set; }

    public decimal FilledQuantity { get; set; }

    public decimal? AverageFillPrice { get; set; }

    public string? ExchangeOrderId { get; set; }

    public required string Source { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
