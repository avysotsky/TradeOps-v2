using TradeOps.Domain.Enums;

namespace TradeOps.Domain.Entities;

public sealed class TradingViewDeliveryHealthState
{
    public const string SingletonId = "TradingView";

    public string Id { get; set; } = SingletonId;

    public TradingViewDeliveryHealthStatus Status { get; set; } =
        TradingViewDeliveryHealthStatus.Unknown;

    public string Reason { get; set; } = "Not evaluated.";

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset WindowFrom { get; set; }

    public DateTimeOffset WindowTo { get; set; }

    public int Total { get; set; }

    public int Failed { get; set; }

    public int Conflict { get; set; }

    public int RiskRejected { get; set; }

    public double? AverageLatencyMilliseconds { get; set; }

    public DateTimeOffset? LatestDeliveryAt { get; set; }

    public DateTimeOffset? LatestSuccessfulAt { get; set; }
}
