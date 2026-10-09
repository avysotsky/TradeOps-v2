namespace TradeOps.Infrastructure.Exchange.Kraken;

public sealed class KrakenFuturesOptions
{
    public const string SectionName = "Exchange:Kraken";

    public string BaseUrl { get; init; } = "https://futures.kraken.com/derivatives/api/v3";

    public string ApiKey { get; init; } = string.Empty;

    public string ApiSecret { get; init; } = string.Empty;

    public string AccountCurrency { get; init; } = "USD";

    public int HttpTimeoutSeconds { get; init; } = 10;
}
