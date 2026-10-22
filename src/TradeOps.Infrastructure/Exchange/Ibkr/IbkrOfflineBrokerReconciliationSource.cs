using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Ibkr;

/// <summary>
/// Read-only bridge from a validated LOCAL SYNTHETIC pagination chain
/// to the broker-neutral Application evidence port. This is not a
/// network-facing IBKR, CPGW, TWS, or authenticated Paper adapter.
/// </summary>
public sealed class IbkrOfflineBrokerReconciliationSource : IBrokerReconciliationSource
{
    public const string ProviderKey = "IbkrOffline";
    private const string SyntheticInstrumentPrefix = "SYNTHETIC-IBKR-SYMBOL:";

    private readonly IbkrOfflineEvidenceSnapshot _snapshot;

    /// <summary>
    /// Accepts only a page COLLECTOR rather than a raw caller-constructed
    /// `OrdersComplete=true` snapshot: incomplete chains cannot be
    /// quietly relabeled as complete at this boundary.
    /// </summary>
    public IbkrOfflineBrokerReconciliationSource(
        IbkrOfflineEvidencePageCollector collector)
    {
        ArgumentNullException.ThrowIfNull(collector);

        // Freeze this observation. Appending more pages to the collector
        // later cannot retroactively upgrade previously incomplete evidence.
        var accepted = collector.IsComplete
            ? collector.RequireCompleteSnapshot()
            : collector.GetPartialSnapshot();

        _snapshot = accepted with
        {
            Orders = Array.AsReadOnly(accepted.Orders.Select(order =>
                order with
                {
                    Executions = Array.AsReadOnly(
                        order.Executions.Select(execution => execution with { })
                            .ToArray())
                }).ToArray())
        };
    }

    /// <summary>
    /// Explicit synthetic-only instrument identity. This is NOT the
    /// official IBKR conid, exchange, currency, trading class or contract
    /// qualification. Do NOT reuse this key for production IBKR.
    /// </summary>
    public static string MakeSyntheticInstrumentKey(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)
            || symbol.Length > 100
            || symbol != symbol.Trim()
            || symbol.Any(char.IsControl))
        {
            throw Invalid();
        }

        return SyntheticInstrumentPrefix + symbol;
    }

    public Task<BrokerOrderReconciliationEvidence> FetchOrderEvidenceAsync(
        BrokerOrderReconciliationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(context.ProviderKey, ProviderKey,
                StringComparison.Ordinal)
            || !string.Equals(context.AccountKey, _snapshot.AccountKey,
                StringComparison.Ordinal))
        {
            throw Invalid();
        }

        var order = _snapshot.Orders.FirstOrDefault(evidence =>
            string.Equals(evidence.ClientOrderId, context.ClientOrderId,
                StringComparison.Ordinal));

        // A client ID absent from the account, but a known broker ID
        // present on another order, is conflicting identity; do not
        // convert it into a benign NotFound.
        if (context.BrokerOrderId is not null
            && _snapshot.Orders.Any(other =>
                string.Equals(other.BrokerOrderId, context.BrokerOrderId,
                    StringComparison.Ordinal)
                && !string.Equals(other.ClientOrderId, context.ClientOrderId,
                    StringComparison.Ordinal)))
        {
            throw Invalid();
        }

        if (!_snapshot.OrdersComplete && order is null)
        {
            return Task.FromResult(Result(
                BrokerEvidenceAvailability.Incomplete, false, null));
        }

        if (order is null)
        {
            return Task.FromResult(Result(
                BrokerEvidenceAvailability.NotFound, false, null));
        }

        // The offline fixture is symbol-based. Requiring a conspicuous
        // synthetic namespace prevents accidental conflation with IBKR's
        // REAL conid/currency/exchange contract identity.
        var syntheticInstrumentKey = MakeSyntheticInstrumentKey(order.Symbol);

        var normalized = new NormalizedBrokerOrderEvidence(
            order.AccountKey,
            order.ClientOrderId,
            order.BrokerOrderId,
            syntheticInstrumentKey,
            order.Side,
            order.RequestedQuantity,
            order.CumulativeFilled,
            MapStatus(order.Status),
            Array.AsReadOnly(order.Executions.Select(execution =>
                new NormalizedBrokerExecutionEvidence(
                    execution.ExecutionId,
                    execution.AccountKey,
                    execution.ClientOrderId,
                    execution.BrokerOrderId,
                    execution.Quantity)).ToArray()));

        return Task.FromResult(Result(
            _snapshot.OrdersComplete && order.ExecutionsComplete
                ? BrokerEvidenceAvailability.Complete
                : BrokerEvidenceAvailability.Incomplete,
            order.ExecutionsComplete,
            normalized));
    }

    private BrokerOrderReconciliationEvidence Result(
        BrokerEvidenceAvailability availability,
        bool executionsComplete,
        NormalizedBrokerOrderEvidence? order) =>
        new(
            availability,
            ProviderKey,
            _snapshot.AccountKey,
            _snapshot.Sequence,
            _snapshot.OrdersComplete,
            executionsComplete,
            order);

    private static OrderStatus MapStatus(IbkrOfflineBrokerOrderStatus status) =>
        status switch
        {
            IbkrOfflineBrokerOrderStatus.PendingSubmit
                or IbkrOfflineBrokerOrderStatus.PreSubmitted
                => OrderStatus.Submitted,
            IbkrOfflineBrokerOrderStatus.Submitted
                => OrderStatus.Accepted,
            IbkrOfflineBrokerOrderStatus.PartiallyFilled
                => OrderStatus.PartiallyFilled,
            IbkrOfflineBrokerOrderStatus.Filled
                => OrderStatus.Filled,
            IbkrOfflineBrokerOrderStatus.Cancelled
                => OrderStatus.Cancelled,
            IbkrOfflineBrokerOrderStatus.Rejected
                => OrderStatus.Rejected,
            IbkrOfflineBrokerOrderStatus.PendingCancel
                or IbkrOfflineBrokerOrderStatus.Unknown
                => OrderStatus.Unknown,
            _ => throw Invalid()
        };

    private static InvalidOperationException Invalid() =>
        new("Offline synthetic IBKR evidence identity mismatch; reconciliation is unresolved.");
}
