namespace TradeOps.Application.Models;

public sealed class TradingViewDeliveryHealthSettings
{
    public const string SectionName = "TradingViewHealth";

    public bool Enabled { get; set; }

    public int EvaluationIntervalSeconds { get; set; } = 60;

    public int LookbackMinutes { get; set; } = 15;

    public int MinimumDeliveries { get; set; } = 5;

    public int CriticalFailureCount { get; set; } = 2;

    public decimal CriticalFailureRatePercent { get; set; } = 20m;

    public decimal DegradedConflictRatePercent { get; set; } = 20m;

    public decimal DegradedRiskRejectedRatePercent { get; set; } = 50m;

    public int MaxAverageLatencyMilliseconds { get; set; } = 1000;

    public int NoSuccessfulDeliveryMinutes { get; set; }
}
