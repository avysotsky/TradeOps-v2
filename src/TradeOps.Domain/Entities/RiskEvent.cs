using TradeOps.Domain.Enums;

namespace TradeOps.Domain.Entities;

public sealed class RiskEvent
{
    public Guid Id { get; set; }

    public required string EventType { get; set; }

    public required string EventKey { get; set; }

    public string? Symbol { get; set; }

    public required string Message { get; set; }

    public RiskEventSeverity Severity { get; set; }

    public OrderSide? LocalSide { get; set; }

    public decimal? LocalQuantity { get; set; }

    public OrderSide? ExchangeSide { get; set; }

    public decimal? ExchangeQuantity { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastObservedAt { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }
}
