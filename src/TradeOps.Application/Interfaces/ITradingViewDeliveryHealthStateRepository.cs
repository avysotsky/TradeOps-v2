using TradeOps.Domain.Entities;

namespace TradeOps.Application.Interfaces;

public interface ITradingViewDeliveryHealthStateRepository
{
    Task<TradingViewDeliveryHealthState?> GetAsync(
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        TradingViewDeliveryHealthState state,
        CancellationToken cancellationToken = default);
}
