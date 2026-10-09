namespace TradeOps.Application.Models;

public sealed record EmergencyStopExecutionResult(
    RiskControlSnapshot Risk,
    BulkOrderCancellationResult? OrderCancellation);
