namespace TradeOps.Infrastructure.Exchange.Okx;

public sealed class OkxOptions
{
    public const string SectionName = "Exchange:Okx";

    public string BaseUrl { get; init; } = "https://openapi.okx.com";

    public string PrivateWebSocketUrl { get; init; } = "wss://wspap.okx.com/ws/v5/private";

    public string ApiKey { get; init; } = string.Empty;

    public string ApiSecret { get; init; } = string.Empty;

    public string Passphrase { get; init; } = string.Empty;

    public string AccountCurrency { get; init; } = "USDT";

    public string TradeMode { get; init; } = "cross";

    public int HttpTimeoutSeconds { get; init; } = 10;

    public int WebSocketPingIntervalSeconds { get; init; } = 20;
}
