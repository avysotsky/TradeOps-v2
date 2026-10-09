namespace TradeOps.Infrastructure.Exchange.Ibkr;

public sealed class IbkrOptions
{
    public const string SectionName = "Exchange:Ibkr";

    public string BaseUrl { get; init; } = "https://localhost:5000/v1/api";

    public string AccountId { get; init; } = string.Empty;

    public string AccountCurrency { get; init; } = "USD";

    public int HttpTimeoutSeconds { get; init; } = 15;

    public bool AllowUntrustedLocalhostCertificate { get; init; } = true;
}
