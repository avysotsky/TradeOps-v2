namespace TradeOps.Application.Models;

public enum EarningsGuidanceDirection
{
    Unknown = 0,
    Lowered = 1,
    Maintained = 2,
    Raised = 3
}

public sealed record EarningsGuidanceSnapshot(
    EarningsGuidanceDirection Direction,
    decimal? RevenueLow = null,
    decimal? RevenueHigh = null,
    decimal? DilutedEpsLow = null,
    decimal? DilutedEpsHigh = null);

public sealed record EarningsSnapshot(
    decimal? Revenue = null,
    decimal? DilutedEps = null,
    decimal? NetIncome = null,
    decimal? GrossMargin = null,
    decimal? OperatingMargin = null,
    EarningsGuidanceSnapshot? Guidance = null);
