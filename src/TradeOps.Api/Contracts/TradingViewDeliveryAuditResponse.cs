using TradeOps.Domain.Enums;

namespace TradeOps.Api.Contracts;

public sealed record TradingViewDeliveryAuditResponse(
    Guid DeliveryId,
    string? EventId,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? CompletedAt,
    long? DurationMilliseconds,
    TradingViewDeliveryOutcome Outcome,
    int? HttpStatusCode,
    Guid? SignalId,
    Guid? OrderId,
    string? ClientOrderId,
    string? Symbol,
    string? Action);
