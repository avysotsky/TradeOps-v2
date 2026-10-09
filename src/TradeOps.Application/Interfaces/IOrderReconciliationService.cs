using TradeOps.Application.Models;

namespace TradeOps.Application.Interfaces;

public interface IOrderReconciliationService
{
    Task<ReconciliationSummary> ReconcileAsync(
        CancellationToken cancellationToken = default);
}
