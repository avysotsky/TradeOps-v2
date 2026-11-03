using System.Text.Json;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Ibkr;

/// <summary>
/// Account-free, synchronous snapshot reconciliation for synthetic, normalized
/// IBKR-shaped evidence. No HTTP, session, credentials or broker mutations.
/// Callers must supply a complete and trusted offline evidence fixture.
/// NOT safe to use as a production or authenticated broker reconciliation service.
/// </summary>
public sealed class IbkrOfflineSnapshotReconciler(IbkrOfflineOrderLifecycle lifecycle)
{
    private const int MaxOrders = 256;
    private const int MaxExecutionsPerOrder = 4096;
    private readonly Dictionary<string, AppliedEvidence> _applied = new(StringComparer.Ordinal);

    public IbkrOfflineReconciliationResult Reconcile(
        string clientOrderId,
        IbkrOfflineEvidenceSnapshot evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientOrderId);
        ArgumentNullException.ThrowIfNull(evidence);

        var current = lifecycle.Get(clientOrderId);
        if (evidence.Sequence <= 0
            || string.IsNullOrWhiteSpace(evidence.AccountKey)
            || evidence.Orders is null
            || evidence.Orders.Count > MaxOrders)
        {
            throw Invalid();
        }

        // Rejected snapshots do not affect either the reducer or replay cache.
        if (!string.Equals(
                evidence.AccountKey,
                current.Intent.AccountKey,
                StringComparison.Ordinal))
        {
            throw Invalid();
        }

        var fingerprint = JsonSerializer.Serialize(evidence);
        if (_applied.TryGetValue(clientOrderId, out var last))
        {
            if (evidence.Sequence < last.Sequence
                || (evidence.Sequence == last.Sequence
                    && !string.Equals(fingerprint, last.Fingerprint, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "Stale or conflicting IBKR offline reconciliation sequence.");
            }

            if (evidence.Sequence == last.Sequence)
            {
                return last.Result; // Replayed identical observation, no second fill.
            }
        }

        if (current.State != IbkrOfflineOrderState.Unknown)
        {
            throw new InvalidOperationException(
                "Offline reconciliation requires an ambiguous Unknown order state.");
        }

        var seenCoids = new HashSet<string>(StringComparer.Ordinal);
        var seenBrokerIds = new HashSet<string>(StringComparer.Ordinal);
        IbkrOfflineBrokerOrderEvidence? matchingOrder = null;

        foreach (var order in evidence.Orders)
        {
            if (order is null
                || string.IsNullOrWhiteSpace(order.ClientOrderId)
                || string.IsNullOrWhiteSpace(order.BrokerOrderId)
                || string.IsNullOrWhiteSpace(order.AccountKey)
                || string.IsNullOrWhiteSpace(order.Symbol)
                || order.RequestedQuantity <= 0m
                || order.CumulativeFilled < 0m
                || order.CumulativeFilled > order.RequestedQuantity
                || !Enum.IsDefined(order.Side)
                || !Enum.IsDefined(order.Status)
                || !string.Equals(order.AccountKey, evidence.AccountKey, StringComparison.Ordinal)
                || !seenCoids.Add(order.ClientOrderId)
                || !seenBrokerIds.Add(order.BrokerOrderId))
            {
                throw Invalid();
            }

            // Matching either identity is sufficient to demand *both*
            // identities and all immutable business terms agree. Otherwise,
            // a wrong-order match could falsely clear an Unknown state.
            var byCoid = string.Equals(
                order.ClientOrderId, clientOrderId, StringComparison.Ordinal);
            var byBrokerId = current.BrokerOrderId is not null
                && string.Equals(
                    order.BrokerOrderId, current.BrokerOrderId, StringComparison.Ordinal);
            if (!byCoid && !byBrokerId)
            {
                continue;
            }

            if (!byCoid
                || (current.BrokerOrderId is not null && !byBrokerId)
                || !string.Equals(order.Symbol, current.Intent.Symbol, StringComparison.Ordinal)
                || order.Side != current.Intent.Side
                || order.RequestedQuantity != current.Intent.Quantity)
            {
                throw Invalid();
            }

            matchingOrder = order;
        }

        IbkrOfflineReconciliationResult result;

        if (!evidence.OrdersComplete)
        {
            // A partial broker response is not proof of absence or status.
            result = new(IbkrOfflineReconciliationDisposition.Inconclusive, current, evidence.Sequence);
        }
        else if (matchingOrder is null)
        {
            // Even a complete "not found" snapshot cannot make re-submit safe.
            var unchanged = lifecycle.Apply(new IbkrOfflineOrderEvent(
                clientOrderId, IbkrOfflineOrderEventType.ReconciliationNotFound));
            result = new(IbkrOfflineReconciliationDisposition.NotFound, unchanged, evidence.Sequence);
        }
        else if (!matchingOrder.ExecutionsComplete
                 || matchingOrder.Executions is null)
        {
            result = new(IbkrOfflineReconciliationDisposition.Inconclusive, current, evidence.Sequence);
        }
        else
        {
            var order = matchingOrder;
            var executed = ValidateExecutionEvidence(order);
            if (executed != order.CumulativeFilled
                || executed < current.CumulativeFilled)
            {
                throw Invalid();
            }

            IbkrOfflineOrderEventType? eventType = order.Status switch
            {
                IbkrOfflineBrokerOrderStatus.PendingSubmit
                    or IbkrOfflineBrokerOrderStatus.PreSubmitted
                    or IbkrOfflineBrokerOrderStatus.Submitted
                    or IbkrOfflineBrokerOrderStatus.PartiallyFilled
                    when executed < order.RequestedQuantity
                        => IbkrOfflineOrderEventType.ReconciledWorking,

                IbkrOfflineBrokerOrderStatus.Filled
                    when executed == order.RequestedQuantity
                        => IbkrOfflineOrderEventType.ReconciledFilled,

                IbkrOfflineBrokerOrderStatus.Cancelled
                    when executed < order.RequestedQuantity
                        => IbkrOfflineOrderEventType.ReconciledCancelled,

                _ => null
            };

            if (order.Status is IbkrOfflineBrokerOrderStatus.PartiallyFilled
                && executed == 0m)
            {
                throw Invalid();
            }

            if (eventType is null)
            {
                // PendingCancel, Unknown or Rejected status (or inconsistent
                // terminal quantity) may not justify a state transition.
                result = new(IbkrOfflineReconciliationDisposition.Inconclusive, current, evidence.Sequence);
            }
            else
            {
                var resolved = lifecycle.Apply(new IbkrOfflineOrderEvent(
                    clientOrderId,
                    eventType.Value,
                    BrokerOrderId: order.BrokerOrderId,
                    CumulativeFilled: executed));

                var disposition = resolved.State switch
                {
                    IbkrOfflineOrderState.Working
                        => IbkrOfflineReconciliationDisposition.Working,
                    IbkrOfflineOrderState.PartiallyFilled
                        => IbkrOfflineReconciliationDisposition.PartiallyFilled,
                    IbkrOfflineOrderState.Filled
                        => IbkrOfflineReconciliationDisposition.Filled,
                    IbkrOfflineOrderState.Cancelled
                        => IbkrOfflineReconciliationDisposition.Cancelled,
                    _ => throw Invalid()
                };

                result = new(disposition, resolved, evidence.Sequence);
            }
        }

        _applied[clientOrderId] = new AppliedEvidence(evidence.Sequence, fingerprint, result);
        return result;
    }

    private static decimal ValidateExecutionEvidence(IbkrOfflineBrokerOrderEvidence order)
    {
        if (order.Executions.Count > MaxExecutionsPerOrder)
        {
            throw Invalid();
        }

        var unique = new Dictionary<string, decimal>(StringComparer.Ordinal);
        decimal total = 0m;

        foreach (var execution in order.Executions)
        {
            if (execution is null
                || string.IsNullOrWhiteSpace(execution.ExecutionId)
                || execution.Quantity <= 0m
                || !string.Equals(execution.AccountKey, order.AccountKey, StringComparison.Ordinal)
                || !string.Equals(execution.ClientOrderId, order.ClientOrderId, StringComparison.Ordinal)
                || !string.Equals(execution.BrokerOrderId, order.BrokerOrderId, StringComparison.Ordinal))
            {
                throw Invalid();
            }

            if (unique.TryGetValue(execution.ExecutionId, out var previous))
            {
                if (previous != execution.Quantity)
                {
                    throw Invalid();
                }

                continue; // Identical replay cannot count a fill twice.
            }

            unique.Add(execution.ExecutionId, execution.Quantity);
            try
            {
                total = checked(total + execution.Quantity);
            }
            catch (OverflowException)
            {
                throw Invalid();
            }
        }

        return total;
    }

    private static InvalidOperationException Invalid() =>
        new("Ambiguous or inconsistent IBKR offline reconciliation evidence; order remains unresolved.");

    private sealed record AppliedEvidence(
        long Sequence,
        string Fingerprint,
        IbkrOfflineReconciliationResult Result);
}

public enum IbkrOfflineBrokerOrderStatus
{
    PendingSubmit,
    PreSubmitted,
    Submitted,
    PartiallyFilled,
    PendingCancel,
    Filled,
    Cancelled,
    Rejected,
    Unknown
}

public enum IbkrOfflineReconciliationDisposition
{
    Inconclusive,
    NotFound,
    Working,
    PartiallyFilled,
    Filled,
    Cancelled
}

/// <summary>
/// Normalized, synthetic full-account snapshot, not a raw IBKR API DTO.
/// Sequence is an explicit offline fixture value, never an inferred clock.
/// </summary>
public sealed record IbkrOfflineEvidenceSnapshot(
    long Sequence,
    string AccountKey,
    bool OrdersComplete,
    IReadOnlyList<IbkrOfflineBrokerOrderEvidence> Orders);

public sealed record IbkrOfflineBrokerOrderEvidence(
    string AccountKey,
    string ClientOrderId,
    string BrokerOrderId,
    string Symbol,
    OrderSide Side,
    decimal RequestedQuantity,
    decimal CumulativeFilled,
    IbkrOfflineBrokerOrderStatus Status,
    bool ExecutionsComplete,
    IReadOnlyList<IbkrOfflineExecutionEvidence> Executions);

public sealed record IbkrOfflineExecutionEvidence(
    string ExecutionId,
    string AccountKey,
    string ClientOrderId,
    string BrokerOrderId,
    decimal Quantity);

public sealed record IbkrOfflineReconciliationResult(
    IbkrOfflineReconciliationDisposition Disposition,
    IbkrOfflineOrderSnapshot Order,
    long Sequence);
