namespace TradeOps.Api.Security;

public sealed class SignalIngressSigningOptions
{
    public const string SectionName = "SignalIngress:Signing";

    public bool Enabled { get; set; }

    public string SignatureHeaderName { get; set; } = "X-TradeOps-Signature";

    public string Secret { get; set; } = string.Empty;

    public int MaxBodyBytes { get; set; } = 65536;
}
