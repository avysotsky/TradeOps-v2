using TradeOps.Application.Models;

namespace TradeOps.Application.Interfaces;

public interface IExecutionMetricsBySymbolRepository
{
    Task<ExecutionMetricsBySymbolSnapshot> GetAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        int limit,
        CancellationToken cancellationToken = default);
}
