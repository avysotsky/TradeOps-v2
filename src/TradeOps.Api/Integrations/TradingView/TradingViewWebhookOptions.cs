namespace TradeOps.Api.Integrations.TradingView;

public sealed class TradingViewWebhookOptions
{
    public const string SectionName = "Integrations:TradingView";

    public bool Enabled { get; set; }

    public string GatewayHeaderName { get; set; } =
        "X-TradeOps-TradingView-Gateway-Key";

    public string GatewayKey { get; set; } = string.Empty;

    public bool RequireGatewayIpAllowlist { get; set; } = true;

    public string[] AllowedGatewayIps { get; set; } =
    [
        "127.0.0.1",
        "::1"
    ];
}
