using TradeOps.Application.Models;

namespace TradeOps.Api.Controllers;

/// <summary>Transport-only risk snapshot DTO with one unambiguous JSON constructor.</summary>
public sealed record ResearchRiskControlsRequest(
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
    public RiskControlSnapshot ToSnapshot()
    {
        if (string.IsNullOrWhiteSpace(SettlementCurrency) ||
            UnconvertedFees is null ||
            ActivePositionMismatchCount < 0 ||
            UpdatedAt == default)
            throw new ArgumentException("Invalid risk controls snapshot.");
        return new RiskControlSnapshot(
            TradingEnabled, EmergencyStop, EmergencyStopReason,
            SettlementCurrency, DailyGrossRealizedPnL, DailySettlementFees,
            DailyNetRealizedPnL, UnconvertedFees,
            ActivePositionMismatchCount, UpdatedAt);
    }

    public static ResearchRiskControlsRequest FromSnapshot(RiskControlSnapshot source) => new(
        source.TradingEnabled, source.EmergencyStop, source.EmergencyStopReason,
        source.SettlementCurrency, source.DailyGrossRealizedPnL,
        source.DailySettlementFees, source.DailyNetRealizedPnL,
        source.UnconvertedFees, source.ActivePositionMismatchCount, source.UpdatedAt);
}
