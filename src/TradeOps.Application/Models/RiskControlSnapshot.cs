namespace TradeOps.Application.Models;

public sealed record RiskControlSnapshot(
    bool TradingEnabled,
    bool EmergencyStop,
    string? EmergencyStopReason,
    string SettlementCurrency,
    decimal DailyGrossRealizedPnL,
    decimal DailySettlementFees,
    decimal? DailyNetRealizedPnL,
    IReadOnlyCollection<UnconvertedFee> UnconvertedFees,
    int ActivePositionMismatchCount,
    DateTimeOffset UpdatedAt)
{
    public RiskControlSnapshot(
        bool tradingEnabled,
        bool emergencyStop,
        string? emergencyStopReason,
        decimal dailyRealizedPnL,
        int activePositionMismatchCount,
        DateTimeOffset updatedAt)
        : this(
            tradingEnabled,
            emergencyStop,
            emergencyStopReason,
            "USDT",
            dailyRealizedPnL,
            0m,
            dailyRealizedPnL,
            Array.Empty<UnconvertedFee>(),
            activePositionMismatchCount,
            updatedAt)
    {
    }

    public bool HasPositionMismatch => ActivePositionMismatchCount > 0;

    public bool IsDailyAccountingComplete => DailyNetRealizedPnL.HasValue;

    // Backward-compatible alias. New risk logic should use DailyNetRealizedPnL explicitly.
    public decimal DailyRealizedPnL => DailyNetRealizedPnL ?? DailyGrossRealizedPnL;
}
