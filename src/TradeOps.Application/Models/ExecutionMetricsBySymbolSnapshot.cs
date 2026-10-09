namespace TradeOps.Application.Models;

public sealed record ExecutionMetricsBySymbolItem(
    string Symbol,
    WindowSignalExecutionMetrics Signals,
    WindowOrderLifecycleMetrics OrderLifecycle,
    long FillsReceived);

public sealed record ExecutionMetricsBySymbolSnapshot(
    DateTimeOffset GeneratedAt,
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    int Limit,
    bool IsTruncated,
    IReadOnlyList<ExecutionMetricsBySymbolItem> Symbols);
