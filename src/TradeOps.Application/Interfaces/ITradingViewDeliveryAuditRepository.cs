using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Interfaces;

public interface ITradingViewDeliveryAuditRepository
{
    Task<Guid> StartAsync(
        string? eventId,
        string? symbol,
        string? action,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        Guid deliveryId,
        TradingViewDeliveryOutcome outcome,
        int httpStatusCode,
        Guid? signalId = null,
        Guid? orderId = null,
        string? clientOrderId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<TradingViewDeliveryAudit>> GetAsync(
        string? eventId,
        int limit,
        CancellationToken cancellationToken = default);

    Task<TradingViewDeliveryMetricsSnapshot> GetMetricsAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        CancellationToken cancellationToken = default);
}
