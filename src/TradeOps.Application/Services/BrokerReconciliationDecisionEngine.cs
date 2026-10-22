using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Services;

/// <summary>
/// Pure, single-order, broker-neutral evaluation. Not registered in runtime DI:
/// no provider network I/O, database changes, or broker trading operations.
/// Broker-native completeness, pagination and identity must be established
/// independently by a future authorized adapter.
/// </summary>
public sealed class BrokerReconciliationDecisionEngine(IOrderStateMachine stateMachine)
    : IBrokerReconciliationDecisionEngine
{
    private const int MaxKeyLength = 128;
    private const int MaxExecutions = 4096;
    private const decimal MaxQuantityExclusive = 10_000_000_000_000_000m;

    public BrokerReconciliationDecision Evaluate(
        BrokerOrderReconciliationContext local,
        BrokerOrderReconciliationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateContext(local);

        if (!Enum.IsDefined(evidence.Availability)
            || !string.Equals(evidence.ProviderKey, local.ProviderKey, StringComparison.Ordinal)
            || !string.Equals(evidence.AccountKey, local.AccountKey, StringComparison.Ordinal))
        {
            throw Invalid();
        }

        if (evidence.Availability is
            BrokerEvidenceAvailability.Unauthorized
            or BrokerEvidenceAvailability.Unavailable
            or BrokerEvidenceAvailability.RateLimited)
        {
            // Failures of transport/session/pacing are NEVER evidence of a
            // missing order, even when a previously cached order was absent.
            if (evidence.Order is not null
                || evidence.ObservationSequence is not null
                || evidence.OrdersComplete
                || evidence.ExecutionsComplete)
            {
                throw Invalid();
            }

            return Unresolved("SourceUnavailableOrUnauthorized");
        }

        if (evidence.ObservationSequence is null
            || evidence.ObservationSequence <= local.LastAcceptedObservationSequence
            || evidence.ObservationSequence <= 0)
        {
            // Equality is deliberately NOT interpreted as an idempotent
            // replay without the durable payload-digest journal.
            throw Invalid();
        }

        if (evidence.Availability is BrokerEvidenceAvailability.NotFound)
        {
            if (!evidence.OrdersComplete
                || evidence.ExecutionsComplete
                || evidence.Order is not null)
            {
                throw Invalid();
            }

            return Unresolved("CompleteNotFoundDoesNotAuthorizeResubmission");
        }

        if (evidence.Availability is BrokerEvidenceAvailability.Incomplete)
        {
            if (evidence.OrdersComplete && evidence.ExecutionsComplete)
            {
                throw Invalid();
            }

            if (evidence.Order is not null)
            {
                ValidateOrderIdentity(local, evidence.Order);
            }

            return Unresolved("IncompleteEvidence");
        }

        if (evidence.Availability != BrokerEvidenceAvailability.Complete)
        {
            throw Invalid();
        }

        if (evidence.Order is null)
        {
            // A null lookup alone is never positive evidence of absence.
            throw Invalid();
        }

        ValidateOrderIdentity(local, evidence.Order);

        if (!evidence.OrdersComplete || !evidence.ExecutionsComplete)
        {
            return Unresolved("IncompleteEvidence");
        }

        var order = evidence.Order;
        var executed = ValidateExecutions(order);
        if (executed != order.CumulativeFilledQuantity
            || executed < local.LocalFilledQuantity)
        {
            throw Invalid();
        }

        if (order.Status is OrderStatus.Unknown)
        {
            return Unresolved("BrokerStatusUnknown");
        }

        // A rejected order may represent a preflight refusal, a live
        // execution rejection, or stale status; dedicated evidence policy
        // is required before recommending a terminal Rejected mutation.
        if (order.Status is OrderStatus.Rejected)
        {
            return Unresolved("BrokerRejectionRequiresPolicy");
        }

        if (order.Status is OrderStatus.Created)
        {
            throw Invalid();
        }

        if (order.Status is OrderStatus.Submitted or OrderStatus.Accepted
            && executed != 0m)
        {
            // A working status with a non-zero execution ledger must be
            // normalized to PartiallyFilled by the source adapter.
            return Unresolved("AmbiguousWorkingStatusWithFills");
        }

        if (order.Status is OrderStatus.PartiallyFilled
            && (executed <= 0m || executed >= order.RequestedQuantity))
        {
            throw Invalid();
        }

        if (order.Status is OrderStatus.Filled
            && executed != order.RequestedQuantity)
        {
            throw Invalid();
        }

        if (order.Status is OrderStatus.Cancelled
            && executed == order.RequestedQuantity)
        {
            // A terminal fully executed order is Filled, not Cancelled.
            throw Invalid();
        }

        if (!stateMachine.CanTransition(local.LocalStatus, order.Status))
        {
            return Unresolved("TransitionNotAllowed");
        }

        if (local.LocalStatus == order.Status
            && local.LocalFilledQuantity == executed
            && local.BrokerOrderId is not null)
        {
            return new(
                BrokerReconciliationDecisionKind.NoChange,
                null, null, "AlreadyAligned");
        }

        return new(
            BrokerReconciliationDecisionKind.Candidate,
            order.Status,
            executed,
            "VerifiedNormalizedEvidenceCandidate");
    }

    private static void ValidateContext(BrokerOrderReconciliationContext local)
    {
        CheckKey(local.ProviderKey);
        CheckKey(local.AccountKey);
        CheckKey(local.ClientOrderId);
        CheckKey(local.InstrumentKey);
        if (local.BrokerOrderId is not null)
        {
            CheckKey(local.BrokerOrderId);
        }

        if (local.LastAcceptedObservationSequence < 0
            || !Enum.IsDefined(local.Side)
            || !Enum.IsDefined(local.LocalStatus)
            || !ValidQuantity(local.RequestedQuantity, false)
            || !ValidQuantity(local.LocalFilledQuantity, true)
            || local.LocalFilledQuantity > local.RequestedQuantity)
        {
            throw Invalid();
        }
    }

    private static void ValidateOrderIdentity(
        BrokerOrderReconciliationContext local,
        NormalizedBrokerOrderEvidence order)
    {
        CheckKey(order.AccountKey);
        CheckKey(order.ClientOrderId);
        CheckKey(order.BrokerOrderId);
        CheckKey(order.InstrumentKey);

        if (order.Executions is null
            || order.Executions.Count > MaxExecutions
            || !string.Equals(local.AccountKey, order.AccountKey, StringComparison.Ordinal)
            || !string.Equals(local.ClientOrderId, order.ClientOrderId, StringComparison.Ordinal)
            || (local.BrokerOrderId is not null
                && !string.Equals(local.BrokerOrderId, order.BrokerOrderId, StringComparison.Ordinal))
            || !string.Equals(local.InstrumentKey, order.InstrumentKey, StringComparison.Ordinal)
            || !Enum.IsDefined(order.Side)
            || !Enum.IsDefined(order.Status)
            || local.Side != order.Side
            || local.RequestedQuantity != order.RequestedQuantity
            || !ValidQuantity(order.CumulativeFilledQuantity, true)
            || order.CumulativeFilledQuantity > order.RequestedQuantity)
        {
            throw Invalid();
        }
    }

    private static decimal ValidateExecutions(NormalizedBrokerOrderEvidence order)
    {
        var ids = new Dictionary<string, decimal>(StringComparer.Ordinal);
        decimal cumulative = 0m;

        foreach (var execution in order.Executions)
        {
            if (execution is null)
            {
                throw Invalid();
            }

            CheckKey(execution.ExecutionId);
            if (!string.Equals(execution.AccountKey, order.AccountKey, StringComparison.Ordinal)
                || !string.Equals(execution.ClientOrderId, order.ClientOrderId, StringComparison.Ordinal)
                || !string.Equals(execution.BrokerOrderId, order.BrokerOrderId, StringComparison.Ordinal)
                || !ValidQuantity(execution.Quantity, false))
            {
                throw Invalid();
            }

            if (ids.TryGetValue(execution.ExecutionId, out var previous))
            {
                if (previous != execution.Quantity)
                {
                    throw Invalid();
                }

                continue; // Byte-equivalent normalized fill replay, count once.
            }

            ids.Add(execution.ExecutionId, execution.Quantity);
            try
            {
                cumulative = checked(cumulative + execution.Quantity);
            }
            catch (OverflowException)
            {
                throw Invalid();
            }

            if (cumulative > order.RequestedQuantity)
            {
                throw Invalid();
            }
        }

        return cumulative;
    }

    private static bool ValidQuantity(decimal value, bool allowZero) =>
        (allowZero ? value >= 0m : value > 0m)
        && value < MaxQuantityExclusive
        && decimal.Round(value, 12) == value;

    private static void CheckKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaxKeyLength
            || value != value.Trim()
            || value.Any(char.IsControl))
        {
            throw Invalid();
        }
    }

    private static BrokerReconciliationDecision Unresolved(string reason) =>
        new(BrokerReconciliationDecisionKind.Unresolved, null, null, reason);

    private static InvalidOperationException Invalid() =>
        new("Conflicting, stale or invalid normalized reconciliation evidence; no mutation permitted.");
}
