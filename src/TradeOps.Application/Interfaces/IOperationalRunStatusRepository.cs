using TradeOps.Application.Models;
using TradeOps.Domain.Entities;

namespace TradeOps.Application.Interfaces;

public interface IOperationalRunStatusRepository
{
    Task<OperationalRunStatus?> GetAsync(
        string runType,
        CancellationToken cancellationToken = default);

    Task MarkStartedAsync(
        string runType,
        CancellationToken cancellationToken = default);

    Task MarkCompletedAsync(
        string runType,
        OperationalRunMetrics metrics,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        string runType,
        string errorMessage,
        CancellationToken cancellationToken = default);
}
