namespace TradeOps.Worker;

public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    public int ReconciliationIntervalSeconds { get; set; } = 30;

    public int InitialRetryDelaySeconds { get; set; } = 2;

    public int MaxRetryDelaySeconds { get; set; } = 30;
}
