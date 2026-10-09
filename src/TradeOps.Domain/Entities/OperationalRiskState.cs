namespace TradeOps.Domain.Entities;

public sealed class OperationalRiskState
{
    public const string DefaultId = "default";

    public string Id { get; set; } = DefaultId;

    public bool TradingEnabled { get; set; }

    public bool EmergencyStop { get; set; }

    public string? EmergencyStopReason { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
