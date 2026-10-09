namespace TradeOps.Application.Models;

public static class OperationalRunTypes
{
    public const string OrderReconciliation = "OrderReconciliation";

    public const string RecoveryCycle = "RecoveryCycle";
}

public sealed record OperationalRunMetrics(
    int OrdersScanned = 0,
    int OrdersUpdated = 0,
    int OrderIssues = 0,
    int OrdersMissingOnExchange = 0,
    int PositionsCompared = 0,
    int PositionMismatches = 0,
    int PositionSnapshots = 0);
