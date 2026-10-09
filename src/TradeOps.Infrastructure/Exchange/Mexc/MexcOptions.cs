namespace TradeOps.Infrastructure.Exchange.Mexc;

public sealed class MexcOptions
{
    public const string SectionName = "Exchange:Mexc";

    public string BaseUrl { get; init; } = "https://contract.mexc.com";

    public string ApiKey { get; init; } = string.Empty;

    public string ApiSecret { get; init; } = string.Empty;

    public string SettlementCurrency { get; init; } = "USDT";

    public int RecvWindowSeconds { get; init; } = 10;

    public int HttpTimeoutSeconds { get; init; } = 10;
}
