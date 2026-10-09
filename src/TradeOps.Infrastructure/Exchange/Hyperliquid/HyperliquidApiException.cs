namespace TradeOps.Infrastructure.Exchange.Hyperliquid;

public sealed class HyperliquidApiException : Exception
{
    public HyperliquidApiException(string message)
        : base(message)
    {
    }
}
