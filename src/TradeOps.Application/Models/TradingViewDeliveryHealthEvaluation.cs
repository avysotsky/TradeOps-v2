using TradeOps.Domain.Enums;

namespace TradeOps.Application.Models;

public sealed record TradingViewDeliveryHealthEvaluation(
    TradingViewDeliveryHealthStatus Status,
    string Reason,
    TradingViewDeliveryMetricsSnapshot Metrics);
