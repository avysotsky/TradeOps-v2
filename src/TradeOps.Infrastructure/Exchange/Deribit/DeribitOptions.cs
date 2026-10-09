namespace TradeOps.Infrastructure.Exchange.Deribit;

public sealed class DeribitOptions
{
    public const string SectionName = "Exchange:Deribit";

    public string BaseUrl { get; init; } = "https://test.deribit.com/api/v2";

    public string WebSocketUrl { get; init; } = "wss://test.deribit.com/ws/api/v2";

    public string ClientId { get; init; } = string.Empty;

    public string ClientSecret { get; init; } = string.Empty;

    public string AccountCurrency { get; init; } = "BTC";

    public int HttpTimeoutSeconds { get; init; } = 10;

    public int WebSocketPingIntervalSeconds { get; init; } = 20;
}
