using TradeOps.Domain.Entities;

namespace TradeOps.Application.Interfaces;

public interface ITradingSignalRepository
{
    Task<TradingSignal?> GetByIdAsync(
        Guid signalId,
        CancellationToken cancellationToken = default);

    Task<bool> TryAddAsync(
        TradingSignal signal,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        TradingSignal signal,
        CancellationToken cancellationToken = default);
}
