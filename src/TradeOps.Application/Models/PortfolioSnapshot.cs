namespace TradeOps.Application.Models;

public sealed record PortfolioPosition(
    InstrumentReference Instrument,
    decimal Quantity);

public sealed record PortfolioSnapshot(
    string BaseCurrency,
    decimal NetAssetValue,
    decimal Cash,
    IReadOnlyList<PortfolioPosition> Positions,
    DateTimeOffset AsOf);
