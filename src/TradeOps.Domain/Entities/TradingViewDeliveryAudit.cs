using TradeOps.Domain.Enums;

namespace TradeOps.Domain.Entities;

public sealed class TradingViewDeliveryAudit
{
    public Guid Id { get; set; }

    public string? EventId { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public long? DurationMilliseconds { get; set; }

    public TradingViewDeliveryOutcome Outcome { get; set; } =
        TradingViewDeliveryOutcome.Received;

    public int? HttpStatusCode { get; set; }

    public Guid? SignalId { get; set; }

    public Guid? OrderId { get; set; }

    public string? ClientOrderId { get; set; }

    public string? Symbol { get; set; }

    public string? Action { get; set; }
}
