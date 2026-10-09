namespace TradeOps.Infrastructure.Exchange.Ibkr;

public sealed record IbkrResolvedInstrument(
    long Conid,
    string Symbol,
    string Currency,
    string Exchange);
