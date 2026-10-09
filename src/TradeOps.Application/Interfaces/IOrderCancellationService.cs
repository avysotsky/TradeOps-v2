using TradeOps.Application.Models;

namespace TradeOps.Application.Interfaces;

public interface IOrderCancellationService
{
    Task<OrderCancellationResult> CancelAsync(
        string idOrClientOrderId,
        CancellationToken cancellationToken = default);
}
