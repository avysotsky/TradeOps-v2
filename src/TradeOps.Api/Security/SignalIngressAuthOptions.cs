namespace TradeOps.Api.Security;

public sealed class SignalIngressAuthOptions
{
    public const string SectionName = "SignalIngress:Authentication";

    public bool Enabled { get; set; }

    public string HeaderName { get; set; } = "X-TradeOps-Api-Key";

    public string ApiKey { get; set; } = string.Empty;
}
