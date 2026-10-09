namespace TradeOps.Infrastructure.Exchange.Hyperliquid;

public sealed class HyperliquidOptions
{
    public const string SectionName = "Exchange:Hyperliquid";

    public string BaseUrl { get; init; } = "https://api.hyperliquid-testnet.xyz";

    public string WebSocketUrl { get; init; } = "wss://api.hyperliquid-testnet.xyz/ws";

    public string UserAddress { get; init; } = string.Empty;

    public string PrivateKey { get; init; } = string.Empty;

    public decimal MarketSlippagePercent { get; init; } = 5m;

    public int HttpTimeoutSeconds { get; init; } = 10;
}
