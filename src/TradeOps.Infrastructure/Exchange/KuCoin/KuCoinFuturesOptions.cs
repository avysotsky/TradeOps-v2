namespace TradeOps.Infrastructure.Exchange.KuCoin;

public sealed class KuCoinFuturesOptions
{
    public const string SectionName = "Exchange:KuCoin";

    public string BaseUrl { get; init; } = "https://api-futures.kucoin.com";

    public string ApiKey { get; init; } = string.Empty;

    public string ApiSecret { get; init; } = string.Empty;

    public string Passphrase { get; init; } = string.Empty;

    public string KeyVersion { get; init; } = "2";

    public string AccountCurrency { get; init; } = "USDT";

    public int HttpTimeoutSeconds { get; init; } = 10;
}
