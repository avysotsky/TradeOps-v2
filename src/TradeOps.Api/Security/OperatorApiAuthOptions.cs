namespace TradeOps.Api.Security;

public sealed class OperatorApiAuthOptions
{
    public const string SectionName = "OperatorApi:Authentication";

    public bool Enabled { get; set; }

    public string HeaderName { get; set; } = "X-TradeOps-Operator-Key";

    public string ApiKey { get; set; } = string.Empty;
}
