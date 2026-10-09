namespace TradeOps.Infrastructure.Exchange.Binance;

public sealed class BinanceOptions
{
    public const string SectionName = "Exchange:Binance";

    public string BaseUrl { get; init; } = "https://testnet.binancefuture.com";

    public string PrivateWebSocketBaseUrl { get; init; } = "wss://stream.binancefuture.com";

    public string ApiKey { get; init; } = string.Empty;

    public string ApiSecret { get; init; } = string.Empty;

    public string SettlementCurrency { get; init; } = "USDT";

    public int RecvWindowMilliseconds { get; init; } = 5_000;

    public int HttpTimeoutSeconds { get; init; } = 10;

    public int ListenKeyKeepaliveMinutes { get; init; } = 30;
}
