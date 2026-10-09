namespace TradeOps.Application.Models;

public sealed record SignalExecutionMetrics(
    long Received,
    long Accepted,
    long Rejected,
    long Pending);

public sealed record CurrentOrderStatusMetrics(
    long Total,
    long Created,
    long Submitted,
    long Accepted,
    long PartiallyFilled,
    long Filled,
    long Cancelled,
    long Rejected,
    long Unknown);

public sealed record ExecutionRiskMetrics(
    bool TradingEnabled,
    bool EmergencyStop);

public sealed record OperationalExecutionMetrics(
    bool? LastReconciliationSucceeded,
    bool? LastRecoverySucceeded,
    int? LastRecoveryPositionMismatches);

public sealed record ExecutionMetricsSnapshot(
    DateTimeOffset GeneratedAt,
    SignalExecutionMetrics Signals,
    CurrentOrderStatusMetrics Orders,
    long FillsReceived,
    ExecutionRiskMetrics Risk,
    OperationalExecutionMetrics Operations);

public sealed record WindowSignalExecutionMetrics(
    long Received,
    long AcceptedCurrentOutcome,
    long RejectedCurrentOutcome,
    long PendingCurrentOutcome);

public sealed record WindowOrderLifecycleMetrics(
    long Events,
    long OrdersTouched,
    long Created,
    long Submitted,
    long Accepted,
    long PartiallyFilled,
    long Filled,
    long Cancelled,
    long Rejected,
    long Unknown);

public sealed record ExecutionMetricsWindowSnapshot(
    DateTimeOffset GeneratedAt,
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    string? Symbol,
    WindowSignalExecutionMetrics Signals,
    WindowOrderLifecycleMetrics OrderLifecycle,
    long FillsReceived);

public sealed record ExecutionMetricsSeriesBucket(
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    WindowSignalExecutionMetrics Signals,
    WindowOrderLifecycleMetrics OrderLifecycle,
    long FillsReceived);

public sealed record ExecutionMetricsSeriesSnapshot(
    DateTimeOffset GeneratedAt,
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    string Bucket,
    string? Symbol,
    IReadOnlyList<ExecutionMetricsSeriesBucket> Buckets);
