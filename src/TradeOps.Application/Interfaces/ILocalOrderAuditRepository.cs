using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Interfaces;

public interface ILocalOrderAuditRepository
{
    Task<IReadOnlyCollection<Order>> GetAsync(
        string? symbol,
        OrderStatus? status,
        DateTimeOffset? fromInclusive,
        DateTimeOffset? toExclusive,
        int limit,
        CancellationToken cancellationToken = default);
}
