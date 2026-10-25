using System;

namespace TradeOps.Domain.Oms;

/// <summary>OMS projection phase; local uncertainty is not an exchange assertion.</summary>
public enum OmsOrderPhase
{
    Created = 1,
    DispatchPending = 2,
    Submitted = 3,
    Accepted = 4,
    PartiallyFilled = 5,
    CancelPending = 6,
    ReplacePending = 7,
    Unknown = 8,
    Filled = 9,
    Cancelled = 10,
    Rejected = 11,
    Expired = 12
}

public enum OmsObservationKind
{
    LocalCommand = 1,
    PrivateOrderReport = 2,
    OrderLookup = 3,
    OrderHistory = 4,
    ExecutionFill = 5,
    OpenOrdersPresence = 6
}

public sealed record OmsEventCursor
{
    public string Stream { get; }
    public long Epoch { get; }
    public long Sequence { get; }

    public OmsEventCursor(string stream, long epoch, long sequence)
    {
        Stream = OmsGuard.Opaque(stream, nameof(stream));
        if (epoch < 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        Epoch = epoch;
        Sequence = sequence;
    }
}

public sealed record OmsLifecycleObservation(
    AccountKey Scope,
    OmsLocalOrderId LocalOrderId,
    OmsOrderPhase Phase,
    decimal CumulativeFilled,
    decimal? LeavesQuantity,
    OrderRevision Revision,
    OmsObservationKind Kind,
    OmsEventCursor? Cursor = null,
    ExchangeOrderKey? ExchangeId = null,
    ClientOrderLabel? ClientLabel = null);

public sealed record OmsOrderState
{
    public OmsOrderIdentity Identity { get; }
    public OmsOrderTerms Terms { get; }
    public OmsOrderPhase Phase { get; }
    public decimal CumulativeFilled { get; }
    public decimal LeavesQuantity { get; }
    public OrderVersion Version { get; }
    public OrderRevision Revision { get; }
    public OmsEventCursor? LastCursor { get; }

    internal OmsOrderState(OmsOrderIdentity identity, OmsOrderTerms terms, OmsOrderPhase phase,
        decimal cumulativeFilled, decimal leavesQuantity, OrderVersion version,
        OrderRevision revision, OmsEventCursor? lastCursor)
    {
        Identity = identity;
        Terms = terms;
        Phase = phase;
        CumulativeFilled = cumulativeFilled;
        LeavesQuantity = leavesQuantity;
        Version = version;
        Revision = revision;
        LastCursor = lastCursor;
    }
}

public enum OmsTransitionOutcome
{
    Applied = 1,
    Duplicate = 2,
    Stale = 3,
    RequiresReconciliation = 4
}

public sealed record OmsTransitionDecision(
    OmsTransitionOutcome Outcome,
    OmsOrderState State,
    string Reason);

/// <summary>
/// Pure projection contract. Not connected to OrderManager or persistence.
/// A decision requiring reconciliation is never written as a successful transition.
/// </summary>
public static class OmsLifecycleContract
{
    public static OmsOrderState Create(OmsOrderIdentity identity, OmsOrderTerms terms)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(terms);
        return new OmsOrderState(identity, terms, OmsOrderPhase.Created, 0m,
            terms.RequestedQuantity, new OrderVersion(0), new OrderRevision(0), null);
    }

    public static bool IsTerminal(OmsOrderPhase status) => status is
        OmsOrderPhase.Filled or OmsOrderPhase.Cancelled or
        OmsOrderPhase.Rejected or OmsOrderPhase.Expired;

    public static bool HasPendingOrUncertainOutcome(OmsOrderPhase status) =>
        status is OmsOrderPhase.DispatchPending or OmsOrderPhase.Submitted
            or OmsOrderPhase.Unknown or OmsOrderPhase.CancelPending or OmsOrderPhase.ReplacePending;

    /// <summary>Absence in a list of OPEN orders never proves cancellation or rejection.</summary>
    public static OmsTransitionDecision MissingFromOpenOrderSnapshot(OmsOrderState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return Reconcile(current, "Missing open-order entry does not establish terminal state.");
    }

    public static OmsTransitionDecision Apply(OmsOrderState current, OmsLifecycleObservation update)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(update);

        if (update.Scope != current.Identity.Scope || update.LocalOrderId != current.Identity.LocalId)
            throw new InvalidOperationException("Order event scope or local identity does not match.");
        if (update.ExchangeId.HasValue &&
            (string.IsNullOrWhiteSpace(update.ExchangeId.Value.Value) ||
             (current.Identity.ExchangeId.HasValue &&
              update.ExchangeId != current.Identity.ExchangeId)))
            return Reconcile(current, "Conflicting exchange order ID within the account scope.");
        if (update.ClientLabel.HasValue &&
            (string.IsNullOrWhiteSpace(update.ClientLabel.Value.Value) ||
             (current.Identity.ClientLabel.HasValue &&
              update.ClientLabel != current.Identity.ClientLabel)))
            return Reconcile(current, "Conflicting client label within the account scope.");
        if (update.Kind == OmsObservationKind.LocalCommand &&
            (update.ExchangeId.HasValue && !current.Identity.ExchangeId.HasValue ||
             update.ClientLabel.HasValue && !current.Identity.ClientLabel.HasValue))
            return Reconcile(current, "Local command cannot bind an unverified remote identity.");
        if (!Enum.IsDefined(update.Phase) || !Enum.IsDefined(update.Kind))
            return Reconcile(current, "Unrecognized phase or observation kind.");
        if (update.CumulativeFilled < 0m || update.CumulativeFilled > current.Terms.RequestedQuantity)
            return Reconcile(current, "Cumulative fill is outside original order quantity.");
        if (update.LeavesQuantity is < 0m || update.LeavesQuantity > current.Terms.RequestedQuantity)
            return Reconcile(current, "Invalid leaves quantity.");
        if (update.Revision.Value < current.Revision.Value)
            return update.CumulativeFilled > current.CumulativeFilled
                ? Reconcile(current, "Late fill on old revision requires execution replay.")
                : new OmsTransitionDecision(OmsTransitionOutcome.Stale, current, "Old order revision.");
        if (update.Revision.Value > current.Revision.Value + 1)
            return Reconcile(current, "Skipped order revision.");
        if (update.Revision.Value > current.Revision.Value &&
            current.Phase != OmsOrderPhase.ReplacePending)
            return Reconcile(current, "Revision advance requires a pending replace.");

        if (current.LastCursor is not null && update.Cursor is not null)
        {
            if (current.LastCursor.Stream != update.Cursor.Stream ||
                current.LastCursor.Epoch != update.Cursor.Epoch)
                return Reconcile(current, "Cursor streams/epochs are not comparable; recover first.");
            if (update.Cursor.Sequence < current.LastCursor.Sequence)
                return new OmsTransitionDecision(OmsTransitionOutcome.Stale, current, "Out-of-order event.");
            if (update.Cursor.Sequence == current.LastCursor.Sequence)
            {
                if ((!current.Identity.ExchangeId.HasValue && update.ExchangeId.HasValue) ||
                    (!current.Identity.ClientLabel.HasValue && update.ClientLabel.HasValue))
                    return Reconcile(current, "Identity binding changed on repeated sequence.");
                return update.Phase == current.Phase &&
                       update.CumulativeFilled == current.CumulativeFilled &&
                       (update.LeavesQuantity is null || update.LeavesQuantity == current.LeavesQuantity) &&
                       update.Revision == current.Revision
                    ? new OmsTransitionDecision(OmsTransitionOutcome.Duplicate, current, "Repeated sequence.")
                    : Reconcile(current, "Same source sequence carries conflicting order state.");
            }
        }

        if (update.CumulativeFilled < current.CumulativeFilled)
            return Reconcile(current, "Cumulative fills must never decrease.");

        if (update.Kind == OmsObservationKind.LocalCommand)
        {
            if (update.Phase is not (OmsOrderPhase.DispatchPending or OmsOrderPhase.Submitted
                or OmsOrderPhase.CancelPending or OmsOrderPhase.ReplacePending or OmsOrderPhase.Unknown))
                return Reconcile(current, "A local command cannot assert an exchange outcome.");
        }
        else if (update.Kind == OmsObservationKind.OpenOrdersPresence)
        {
            if (update.Phase is not (OmsOrderPhase.Accepted or OmsOrderPhase.PartiallyFilled))
                return Reconcile(current, "Open-order listing cannot attest a terminal order.");
        }
        else if (update.Kind == OmsObservationKind.ExecutionFill &&
                 update.Phase is not (OmsOrderPhase.PartiallyFilled or OmsOrderPhase.Filled or OmsOrderPhase.Cancelled))
        {
            return Reconcile(current, "Execution evidence cannot establish this order phase.");
        }

        if (current.Phase == OmsOrderPhase.Unknown &&
            update.Kind is OmsObservationKind.LocalCommand or OmsObservationKind.OpenOrdersPresence)
            return Reconcile(current, "Unknown remote outcome requires authoritative recovery.");

        if (!CanMove(current.Phase, update.Phase))
            return Reconcile(current, "Invalid or conflicting lifecycle transition.");

        if (update.Phase == OmsOrderPhase.Filled &&
            update.CumulativeFilled != current.Terms.RequestedQuantity)
            return Reconcile(current, "Filled must account for full requested quantity.");
        if (update.Phase == OmsOrderPhase.PartiallyFilled &&
            (update.CumulativeFilled <= 0m ||
             update.CumulativeFilled >= current.Terms.RequestedQuantity))
            return Reconcile(current, "Partial fill must be strictly between zero and requested quantity.");
        if (update.Phase is OmsOrderPhase.Created or OmsOrderPhase.Accepted or OmsOrderPhase.Submitted
            or OmsOrderPhase.DispatchPending && update.CumulativeFilled != 0m)
            return Reconcile(current, "Non-fill phase cannot assert execution quantity.");

        if (IsTerminal(current.Phase))
        {
            // Late executions after confirmed cancellation may increase cumulative fills,
            // but must never reopen or reclassify the canceled order.
            var lateFill = current.Phase == OmsOrderPhase.Cancelled &&
                update.Phase == OmsOrderPhase.Cancelled &&
                update.CumulativeFilled > current.CumulativeFilled &&
                update.Kind is OmsObservationKind.ExecutionFill or OmsObservationKind.OrderHistory;
            if (update.CumulativeFilled != current.CumulativeFilled && !lateFill)
                return Reconcile(current, "Terminal fill history changed unexpectedly.");
        }

        var leaves = IsTerminal(update.Phase) ? 0m
            : update.Phase == OmsOrderPhase.Unknown
                ? current.LeavesQuantity
                : current.Terms.RequestedQuantity - update.CumulativeFilled;
        if (update.LeavesQuantity.HasValue && update.LeavesQuantity.Value != leaves)
            return Reconcile(current, "Reported leaves and cumulative quantity disagree.");

        var identityChanged = (!current.Identity.ExchangeId.HasValue && update.ExchangeId.HasValue) ||
            (!current.Identity.ClientLabel.HasValue && update.ClientLabel.HasValue);
        if (update.Phase == current.Phase &&
            update.CumulativeFilled == current.CumulativeFilled &&
            leaves == current.LeavesQuantity &&
            update.Revision == current.Revision &&
            update.Cursor is null && !identityChanged)
            return new OmsTransitionDecision(OmsTransitionOutcome.Duplicate, current, "Repeated state.");

        var identity = identityChanged
            ? new OmsOrderIdentity(current.Identity.Scope, current.Identity.LocalId,
                current.Identity.IntentId, update.ClientLabel ?? current.Identity.ClientLabel,
                update.ExchangeId ?? current.Identity.ExchangeId)
            : current.Identity;
        var next = new OmsOrderState(identity, current.Terms,
            update.Phase, update.CumulativeFilled, leaves,
            new OrderVersion(checked(current.Version.Value + 1)), update.Revision,
            update.Cursor ?? current.LastCursor);
        return new OmsTransitionDecision(OmsTransitionOutcome.Applied, next, "Valid transition.");
    }

    private static bool CanMove(OmsOrderPhase from, OmsOrderPhase to)
    {
        if (from == to) return true;
        return from switch
        {
            OmsOrderPhase.Created => to is OmsOrderPhase.DispatchPending or OmsOrderPhase.Submitted
                or OmsOrderPhase.Accepted or OmsOrderPhase.PartiallyFilled
                or OmsOrderPhase.Filled or OmsOrderPhase.Cancelled or OmsOrderPhase.Rejected
                or OmsOrderPhase.Unknown,
            OmsOrderPhase.DispatchPending => to is OmsOrderPhase.Submitted or OmsOrderPhase.Accepted
                or OmsOrderPhase.PartiallyFilled or OmsOrderPhase.Filled or OmsOrderPhase.Rejected
                or OmsOrderPhase.Cancelled or OmsOrderPhase.Unknown,
            OmsOrderPhase.Submitted => to is OmsOrderPhase.Accepted or OmsOrderPhase.PartiallyFilled
                or OmsOrderPhase.Filled or OmsOrderPhase.Rejected or OmsOrderPhase.Cancelled
                or OmsOrderPhase.CancelPending or OmsOrderPhase.ReplacePending or OmsOrderPhase.Unknown,
            OmsOrderPhase.Accepted => to is OmsOrderPhase.PartiallyFilled or OmsOrderPhase.Filled
                or OmsOrderPhase.Cancelled or OmsOrderPhase.Expired or OmsOrderPhase.CancelPending
                or OmsOrderPhase.ReplacePending or OmsOrderPhase.Unknown,
            OmsOrderPhase.PartiallyFilled => to is OmsOrderPhase.Filled or OmsOrderPhase.Cancelled
                or OmsOrderPhase.Expired or OmsOrderPhase.CancelPending or OmsOrderPhase.ReplacePending
                or OmsOrderPhase.Unknown,
            OmsOrderPhase.CancelPending => to is OmsOrderPhase.PartiallyFilled or OmsOrderPhase.Filled
                or OmsOrderPhase.Cancelled or OmsOrderPhase.Accepted or OmsOrderPhase.Unknown,
            OmsOrderPhase.ReplacePending => to is OmsOrderPhase.Accepted or OmsOrderPhase.PartiallyFilled
                or OmsOrderPhase.Filled or OmsOrderPhase.Cancelled or OmsOrderPhase.CancelPending
                or OmsOrderPhase.Unknown,
            OmsOrderPhase.Unknown => to is OmsOrderPhase.Submitted or OmsOrderPhase.Accepted
                or OmsOrderPhase.PartiallyFilled or OmsOrderPhase.Filled or OmsOrderPhase.Cancelled
                or OmsOrderPhase.Rejected or OmsOrderPhase.Expired,
            _ => false
        };
    }

    private static OmsTransitionDecision Reconcile(OmsOrderState current, string reason) =>
        new(OmsTransitionOutcome.RequiresReconciliation, current, reason);
}

public enum OmsCommandDispatchPhase
{
    Prepared = 1,
    PersistedNotSent = 2,
    DispatchStarted = 3,
    Acknowledged = 4,
    Rejected = 5,
    Ambiguous = 6
}

public sealed record OmsCommandCorrelation
{
    public AccountKey Scope { get; }
    public OrderIntentId Intent { get; }
    public OmsCommandId Command { get; }
    public DispatchAttempt Attempt { get; }
    public OmsCommandDispatchPhase DispatchPhase { get; }

    public OmsCommandCorrelation(AccountKey scope, OrderIntentId intent, OmsCommandId command,
        DispatchAttempt attempt, OmsCommandDispatchPhase dispatchPhase)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        OmsGuard.NonEmpty(intent.Value, nameof(intent));
        OmsGuard.NonEmpty(command.Value, nameof(command));
        if (attempt.Value <= 0) throw new ArgumentOutOfRangeException(nameof(attempt));
        if (!Enum.IsDefined(dispatchPhase)) throw new ArgumentOutOfRangeException(nameof(dispatchPhase));
        Intent = intent;
        Command = command;
        Attempt = attempt;
        DispatchPhase = dispatchPhase;
    }
}

public static class OmsDispatchSafety
{
    /// <summary>Only a provably unsent command is eligible for the SAME-attempt send.</summary>
    public static bool IsProvablyNotSent(OmsCommandDispatchPhase phase) =>
        phase is OmsCommandDispatchPhase.Prepared or OmsCommandDispatchPhase.PersistedNotSent;
}
