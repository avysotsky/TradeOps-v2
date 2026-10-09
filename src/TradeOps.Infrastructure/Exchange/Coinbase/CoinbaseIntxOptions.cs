namespace TradeOps.Infrastructure.Exchange.Coinbase;

public sealed class CoinbaseIntxOptions
{
    public const string SectionName = "Exchange:CoinbaseIntx";

    public string BaseUrl { get; init; } = "https://api-n5e1.coinbase.com/api/v1";

    public string AccessKey { get; init; } = string.Empty;

    public string Passphrase { get; init; } = string.Empty;

    public string SigningKey { get; init; } = string.Empty;

    public string PortfolioId { get; init; } = string.Empty;

    public string AccountCurrency { get; init; } = "USDC";

    public int HttpTimeoutSeconds { get; init; } = 10;
}
