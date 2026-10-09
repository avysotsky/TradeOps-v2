namespace TradeOps.Application.Models;

public sealed record ExchangeCapabilityProfile(
    string Provider,
    string Venue,
    string Environment,
    bool UsesLiveTradingHost,
    bool SupportsAccountRead,
    bool SupportsPositionRead,
    bool SupportsOpenOrdersRead,
    bool SupportsOrderLookup,
    bool SupportsOrderPlacement,
    bool SupportsOrderCancellation,
    bool SupportsPrivateEventStream,
    string Transport)
{
    public bool IsExecutionEnabled =>
        SupportsOrderPlacement
        && SupportsOrderCancellation;
}
