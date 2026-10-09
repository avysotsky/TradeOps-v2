namespace TradeOps.Application.Models;

public sealed record RebalanceConstraints(
    decimal MaxTargetWeight = 1m,
    decimal MinimumTradeNotional = 0m,
    decimal MinimumCashReserve = 0m,
    decimal QuantityTolerance = 0m,
    TimeSpan? MaximumDecisionAge = null);
