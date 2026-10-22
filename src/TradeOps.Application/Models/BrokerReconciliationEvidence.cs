using TradeOps.Domain.Enums;

namespace TradeOps.Application.Models;

/// <summary>
/// A broker-neutral, account-scoped identity for an already-persisted
/// order. InstrumentKey is the adapter-normalized immutable contract
/// identity, NOT a display ticker alone. Sequence 0 means no accepted
/// observation has yet been persisted.
/// </summary>
public sealed record BrokerOrderReconciliationContext(
    string ProviderKey,
    string AccountKey,
    string ClientOrderId,
    string? BrokerOrderId,
    string InstrumentKey,
    OrderSide Side,
    decimal RequestedQuantity,
    OrderStatus LocalStatus,
    decimal LocalFilledQuantity,
    long LastAcceptedObservationSequence);

/// <summary>
/// Exactly one observation category. A null order is NOT proof of absence:
/// only NotFound with an explicitly complete order-list snapshot can make
/// an absence statement, and even that cannot authorize a re-submission.
/// </summary>
public enum BrokerEvidenceAvailability
{
    Complete = 1,
    NotFound = 2,
    Incomplete = 3,
    Unauthorized = 4,
    Unavailable = 5,
    RateLimited = 6
}

/// <summary>
/// Provider-specific HTTP statuses, cursors and retries MUST be classified
/// by a source adapter before entering this model. The flags assert local
/// completeness only; an unauthenticated adapter cannot create trusted
/// broker evidence by setting them true.
/// </summary>
public sealed record BrokerOrderReconciliationEvidence(
    BrokerEvidenceAvailability Availability,
    string ProviderKey,
    string AccountKey,
    long? ObservationSequence,
    bool OrdersComplete,
    bool ExecutionsComplete,
    NormalizedBrokerOrderEvidence? Order);

public sealed record NormalizedBrokerOrderEvidence(
    string AccountKey,
    string ClientOrderId,
    string BrokerOrderId,
    string InstrumentKey,
    OrderSide Side,
    decimal RequestedQuantity,
    decimal CumulativeFilledQuantity,
    OrderStatus Status,
    IReadOnlyList<NormalizedBrokerExecutionEvidence> Executions);

public sealed record NormalizedBrokerExecutionEvidence(
    string ExecutionId,
    string AccountKey,
    string ClientOrderId,
    string BrokerOrderId,
    decimal Quantity);

/// <summary>
/// Candidate is a validated in-memory proposal, NOT authorization to
/// modify a database row or place, cancel or retry a broker order.
/// Future atomic persistence must independently enforce concurrency,
/// account identity, execution IDs and last observation sequence.
/// </summary>
public enum BrokerReconciliationDecisionKind
{
    Candidate = 1,
    NoChange = 2,
    Unresolved = 3
}

public sealed record BrokerReconciliationDecision(
    BrokerReconciliationDecisionKind Kind,
    OrderStatus? CandidateStatus,
    decimal? CandidateFilledQuantity,
    string ReasonCode);
