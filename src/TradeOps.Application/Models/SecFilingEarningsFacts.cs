namespace TradeOps.Application.Models;

public sealed record SecFilingEarningsFacts(
    string EventId,
    string Symbol,
    string FiscalPeriod,
    string Cik,
    string AccessionNumber,
    string FormType,
    Uri SourceUri,
    DateTimeOffset AcceptedAt,
    string Currency,
    decimal? Revenue = null,
    decimal? DilutedEps = null,
    decimal? NetIncome = null,
    decimal? GrossProfit = null,
    decimal? OperatingIncome = null,
    EarningsGuidanceSnapshot? Guidance = null,
    DateTimeOffset? PubliclyAvailableAt = null);
