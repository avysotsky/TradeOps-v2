namespace TradeOps.Application.Models;

public sealed record SignalExecutionResult(
    Guid SignalId,
    bool Accepted,
    IReadOnlyCollection<string> RiskReasons,
    OrderResult? Order);
