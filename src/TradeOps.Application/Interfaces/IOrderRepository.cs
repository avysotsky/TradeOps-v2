using TradeOps.Domain.Entities;

namespace TradeOps.Application.Interfaces;

public interface IOrderRepository
{
    Task<Order?> GetByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default);

    Task<Order?> GetByExchangeOrderIdAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<Order?>(null);

    Task<IReadOnlyCollection<Order>> GetReconciliationCandidatesAsync(
        CancellationToken cancellationToken = default);

    Task<bool> TryAddAsync(
        Order order,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        Order order,
        CancellationToken cancellationToken = default);
}
