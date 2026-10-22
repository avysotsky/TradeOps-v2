using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;

namespace TradeOps.Infrastructure.Exchange;

/// <summary>
/// Caller-supplied synthetic evidence for the isolated Mock provider.
/// This is NOT a snapshot from a trading venue or a proof that an
/// authenticated exchange returned all orders.
/// </summary>
public sealed record MockBrokerEvidenceSnapshot(
    string AccountKey,
    long ObservationSequence,
    bool OrdersComplete,
    bool ExecutionsComplete,
    IReadOnlyList<NormalizedBrokerOrderEvidence> Orders);

/// <summary>
/// Read-only broker-neutral source for a frozen, synthetic Mock fixture.
/// Does NOT wrap MockExchangeClient: its GetOrder methods advance simulated
/// fills on read and it does not retain an execution-ID ledger.
/// No broker/network/database calls or state mutations.
/// </summary>
public sealed class MockBrokerReconciliationSource : IBrokerReconciliationSource
{
    public const string ProviderKey = "Mock";

    private const int MaxOrders = 256;
    private const int MaxExecutions = 4096;
    private readonly MockBrokerEvidenceSnapshot _snapshot;

    public MockBrokerReconciliationSource(MockBrokerEvidenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.AccountKey)
            || snapshot.AccountKey.Length > 128
            || snapshot.ObservationSequence <= 0
            || snapshot.Orders is null
            || snapshot.Orders.Count > MaxOrders)
        {
            throw Invalid();
        }

        var coids = new HashSet<string>(StringComparer.Ordinal);
        var brokerIds = new HashSet<string>(StringComparer.Ordinal);
        var accepted = new List<NormalizedBrokerOrderEvidence>(snapshot.Orders.Count);
        var count = 0;

        foreach (var order in snapshot.Orders)
        {
            if (order is null
                || order.Executions is null
                || order.Executions.Count > MaxExecutions - count
                || !string.Equals(order.AccountKey, snapshot.AccountKey,
                    StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(order.ClientOrderId)
                || string.IsNullOrWhiteSpace(order.BrokerOrderId)
                || !coids.Add(order.ClientOrderId)
                || !brokerIds.Add(order.BrokerOrderId))
            {
                throw Invalid();
            }

            count += order.Executions.Count;
            var executions = new List<NormalizedBrokerExecutionEvidence>(
                order.Executions.Count);
            foreach (var execution in order.Executions)
            {
                if (execution is null)
                {
                    throw Invalid();
                }

                executions.Add(execution with { });
            }

            accepted.Add(order with
            {
                Executions = Array.AsReadOnly(executions.ToArray())
            });
        }

        _snapshot = snapshot with
        {
            Orders = Array.AsReadOnly(accepted.ToArray())
        };
    }

    public Task<BrokerOrderReconciliationEvidence> FetchOrderEvidenceAsync(
        BrokerOrderReconciliationContext order,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        cancellationToken.ThrowIfCancellationRequested();

        // A context from a different provider/account is NEVER a valid
        // absence lookup in the local fixture. Return failure, not NotFound.
        if (!string.Equals(order.ProviderKey, ProviderKey, StringComparison.Ordinal)
            || !string.Equals(order.AccountKey, _snapshot.AccountKey,
                StringComparison.Ordinal))
        {
            throw Invalid();
        }

        var match = _snapshot.Orders.FirstOrDefault(x =>
            string.Equals(x.ClientOrderId, order.ClientOrderId,
                StringComparison.Ordinal));

        // Identity collisions cannot be downgraded to a missing order.
        if (order.BrokerOrderId is not null
            && _snapshot.Orders.Any(x =>
                string.Equals(x.BrokerOrderId, order.BrokerOrderId,
                    StringComparison.Ordinal)
                && !string.Equals(x.ClientOrderId, order.ClientOrderId,
                    StringComparison.Ordinal)))
        {
            throw Invalid();
        }

        if (!_snapshot.OrdersComplete)
        {
            return Task.FromResult(Result(
                BrokerEvidenceAvailability.Incomplete, match));
        }

        if (match is null)
        {
            return Task.FromResult(new BrokerOrderReconciliationEvidence(
                BrokerEvidenceAvailability.NotFound,
                ProviderKey,
                _snapshot.AccountKey,
                _snapshot.ObservationSequence,
                OrdersComplete: true,
                ExecutionsComplete: false,
                Order: null));
        }

        return Task.FromResult(Result(
            _snapshot.ExecutionsComplete
                ? BrokerEvidenceAvailability.Complete
                : BrokerEvidenceAvailability.Incomplete,
            match));
    }

    private BrokerOrderReconciliationEvidence Result(
        BrokerEvidenceAvailability availability,
        NormalizedBrokerOrderEvidence? order) =>
        new(
            availability,
            ProviderKey,
            _snapshot.AccountKey,
            _snapshot.ObservationSequence,
            _snapshot.OrdersComplete,
            _snapshot.ExecutionsComplete,
            order);

    private static InvalidOperationException Invalid() =>
        new("Mock reconciliation fixture is untrusted or identity-scoped incorrectly.");
}
