namespace TradeOps.Application.Models;

public sealed record SignalTransitionMetricsWindowSnapshot(
    DateTimeOffset GeneratedAt,
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    string? Symbol,
    long ReceivedTransitions,
    long AcceptedTransitions,
    long RejectedTransitions);

public sealed record SignalTransitionMetricsSeriesBucket(
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    long ReceivedTransitions,
    long AcceptedTransitions,
    long RejectedTransitions);

public sealed record SignalTransitionMetricsSeriesSnapshot(
    DateTimeOffset GeneratedAt,
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    string Bucket,
    string? Symbol,
    IReadOnlyList<SignalTransitionMetricsSeriesBucket> Buckets);

public sealed record SignalTransitionMetricsBySymbolItem(
    string Symbol,
    long TotalTransitions,
    long ReceivedTransitions,
    long AcceptedTransitions,
    long RejectedTransitions);

public sealed record SignalTransitionMetricsBySymbolSnapshot(
    DateTimeOffset GeneratedAt,
    DateTimeOffset FromInclusive,
    DateTimeOffset ToExclusive,
    int Limit,
    bool IsTruncated,
    IReadOnlyList<SignalTransitionMetricsBySymbolItem> Symbols);
