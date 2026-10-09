using TradeOps.Application.Models;

namespace TradeOps.Application.Interfaces;

public interface IOrderBulkCancellationService
{
    Task<BulkOrderCancellationResult> CancelOpenOrdersAsync(
        string? symbol = null,
        CancellationToken cancellationToken = default);
}
