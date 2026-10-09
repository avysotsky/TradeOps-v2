namespace TradeOps.Application.Models;

public sealed record SecStructuredFact(
    string Namespace,
    string Concept,
    string Unit,
    decimal Value,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string AccessionNumber,
    string FormType,
    string? DimensionKey = null);

public sealed record SecStructuredFiling(
    string EventId,
    string Symbol,
    string FiscalPeriod,
    string Cik,
    string AccessionNumber,
    string FormType,
    Uri SourceUri,
    DateTimeOffset AcceptedAt,
    DateOnly FiscalPeriodStart,
    DateOnly FiscalPeriodEnd,
    string Currency,
    IReadOnlyList<SecStructuredFact> Facts,
    EarningsGuidanceSnapshot? Guidance = null,
    DateTimeOffset? PubliclyAvailableAt = null);
