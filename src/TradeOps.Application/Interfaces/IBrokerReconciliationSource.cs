using TradeOps.Application.Models;

namespace TradeOps.Application.Interfaces;

/// <summary>
/// Broker-specific read-only evidence port. Implementations may use
/// authorized venue APIs, but this contract cannot place, cancel or retry
/// an order. Response completeness and provider/account binding must be
/// established by the adapter, not inferred from a null lookup.
/// </summary>
public interface IBrokerReconciliationSource
{
    Task<BrokerOrderReconciliationEvidence> FetchOrderEvidenceAsync(
        BrokerOrderReconciliationContext order,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Shared broker-neutral PURE decision engine; does not query providers,
/// modify persistence, send alerts, or authorize order submission.
/// </summary>
public interface IBrokerReconciliationDecisionEngine
{
    BrokerReconciliationDecision Evaluate(
        BrokerOrderReconciliationContext local,
        BrokerOrderReconciliationEvidence evidence);
}
