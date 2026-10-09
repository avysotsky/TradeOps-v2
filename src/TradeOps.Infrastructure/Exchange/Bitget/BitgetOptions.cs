namespace TradeOps.Infrastructure.Exchange.Bitget;

public sealed class BitgetOptions
{
    public const string SectionName = "Exchange:Bitget";

    public string BaseUrl { get; init; } = "https://api.bitget.com";

    public string ApiKey { get; init; } = string.Empty;

    public string ApiSecret { get; init; } = string.Empty;

    public string Passphrase { get; init; } = string.Empty;

    public string Category { get; init; } = "USDT-FUTURES";

    public string AccountCurrency { get; init; } = "USDT";

    public string MarginMode { get; init; } = "crossed";

    public int HttpTimeoutSeconds { get; init; } = 10;
}
