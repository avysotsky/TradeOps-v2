using TradeOps.Domain.Entities;

namespace TradeOps.Application.Interfaces;

public interface IRiskEventRepository
{
    Task<RiskEvent?> GetActiveAsync(
        string eventType,
        string eventKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<RiskEvent>> GetActiveByTypeAsync(
        string eventType,
        CancellationToken cancellationToken = default);

    Task<bool> TryAddAsync(
        RiskEvent riskEvent,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        RiskEvent riskEvent,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<RiskEvent>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken = default);
}
