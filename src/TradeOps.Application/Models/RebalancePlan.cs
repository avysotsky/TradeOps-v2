namespace TradeOps.Application.Models;

public enum RebalancePlanStatus
{
    Ready = 1,
    NoAction = 2,
    Blocked = 3
}

public sealed record RebalanceConstraintViolation(
    string Code,
    string Message);

public sealed record RebalancePlan(
    string DecisionId,
    string StrategyId,
    DateTimeOffset PlannedAt,
    TargetPosition? TargetPosition,
    decimal? CurrentQuantity,
    decimal? CurrentNotional,
    decimal? DeltaQuantity,
    decimal? DeltaNotional,
    RebalancePlanStatus Status,
    RebalanceOrderIntent? OrderIntent,
    IReadOnlyList<RebalanceConstraintViolation> ConstraintViolations);
