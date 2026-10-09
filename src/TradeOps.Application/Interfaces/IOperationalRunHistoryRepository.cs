using TradeOps.Domain.Entities;

namespace TradeOps.Application.Interfaces;

public interface IOperationalRunHistoryRepository
{
    Task<IReadOnlyCollection<OperationalRunRecord>> GetRecentAsync(
        string? runType,
        int limit,
        CancellationToken cancellationToken = default);
}
