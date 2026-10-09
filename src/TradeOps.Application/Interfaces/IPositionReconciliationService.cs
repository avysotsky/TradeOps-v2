using TradeOps.Application.Models;

namespace TradeOps.Application.Interfaces;

public interface IPositionReconciliationService
{
    Task<PositionReconciliationSummary> ReconcileAsync(
        CancellationToken cancellationToken = default);
}
