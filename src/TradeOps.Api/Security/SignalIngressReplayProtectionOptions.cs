namespace TradeOps.Api.Security;

public sealed class SignalIngressReplayProtectionOptions
{
    public const string SectionName = "SignalIngress:ReplayProtection";

    public bool Enabled { get; set; }

    public string TimestampHeaderName { get; set; } = "X-TradeOps-Timestamp";

    public string RequestIdHeaderName { get; set; } = "X-TradeOps-Request-Id";

    public int AllowedClockSkewSeconds { get; set; } = 300;

    public int ReceiptRetentionSeconds { get; set; } = 600;
}
