using TradeOps.Application.Models;

namespace TradeOps.Application.Interfaces;

public interface IExecutionMetricsRepository
{
    Task<ExecutionMetricsSnapshot> GetAsync(
        CancellationToken cancellationToken = default);

    Task<ExecutionMetricsWindowSnapshot> GetWindowAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        string? symbol = null,
        CancellationToken cancellationToken = default);

    Task<ExecutionMetricsSeriesSnapshot> GetSeriesAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        string bucket,
        TimeSpan bucketSize,
        string? symbol = null,
        CancellationToken cancellationToken = default);
}
