namespace TradeOps.Application.Models;

public sealed record EarningsEvent(
    string EventId,
    InstrumentReference Instrument,
    DateTimeOffset PublishedAt,
    string FiscalPeriod,
    EarningsSnapshot Snapshot,
    ResearchSourceProvenance Provenance);
