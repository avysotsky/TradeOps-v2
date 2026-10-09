namespace TradeOps.Application.Models;

public sealed record TradingViewDeliveryMetricsSnapshot(
    DateTimeOffset GeneratedAt,
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    int Total,
    int Pending,
    int Accepted,
    int Redelivered,
    int ValidationRejected,
    int RiskRejected,
    int Conflict,
    int Failed,
    double? AverageLatencyMilliseconds,
    long? MaxLatencyMilliseconds,
    DateTimeOffset? LatestDeliveryAt,
    DateTimeOffset? LatestSuccessfulAt);
