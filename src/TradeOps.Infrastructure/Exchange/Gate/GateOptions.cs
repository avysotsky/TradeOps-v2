namespace TradeOps.Infrastructure.Exchange.Gate;

public sealed class GateOptions
{
    public const string SectionName = "Exchange:Gate";

    public string BaseUrl { get; init; } = "https://api-testnet.gateapi.io/api/v4";

    public string ApiKey { get; init; } = string.Empty;

    public string ApiSecret { get; init; } = string.Empty;

    public string Settle { get; init; } = "usdt";

    public int HttpTimeoutSeconds { get; init; } = 10;
}
