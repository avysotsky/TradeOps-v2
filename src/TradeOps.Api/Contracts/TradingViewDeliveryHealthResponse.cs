using TradeOps.Domain.Enums;

namespace TradeOps.Api.Contracts;

public sealed record TradingViewDeliveryHealthResponse(
    TradingViewDeliveryHealthStatus Status,
    string Reason,
    DateTimeOffset UpdatedAt,
    DateTimeOffset WindowFrom,
    DateTimeOffset WindowTo,
    int Total,
    int Failed,
    int Conflict,
    int RiskRejected,
    double? AverageLatencyMilliseconds,
    DateTimeOffset? LatestDeliveryAt,
    DateTimeOffset? LatestSuccessfulAt);
