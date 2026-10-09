using TradeOps.Domain.Entities;

namespace TradeOps.Application.Interfaces;

public interface ITradingSignalOutcomeHistoryRepository
{
    Task<IReadOnlyCollection<TradingSignalOutcomeEvent>> GetBySignalIdAsync(
        Guid signalId,
        CancellationToken cancellationToken = default);
}
