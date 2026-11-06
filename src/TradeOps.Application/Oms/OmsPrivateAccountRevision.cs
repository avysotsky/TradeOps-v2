using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

/// <summary>Live process-local revision derived from an owned drained cut. New ingress invalidates
/// it before durable capture completes. This is neither a database cursor nor physical-send fencing.</summary>
public sealed class OmsPrivateAccountRevision
{
    internal OmsPrivateCaptureCoordinator Owner { get; }
    internal OmsPrivateAccountSubscriptions Subscriptions { get; }
    public AccountKey Scope => Subscriptions.Scope;
    public Guid Epoch => Subscriptions.Epoch;
    public Guid WindowId { get; }
    public long ThroughReceipt { get; }
    public long EconomicGeneration { get; }
    public OmsAccountSubscriptionProfile Profile => Subscriptions.Profile;
    internal OmsPrivateAccountRevision(OmsPrivateCaptureCoordinator owner, OmsPrivateAccountSubscriptions subscriptions,
        Guid windowId, long through, long economicGeneration)
    { Owner = owner; Subscriptions = subscriptions; WindowId = windowId; ThroughReceipt = through; EconomicGeneration = economicGeneration; }
}

public sealed partial class OmsPrivateCaptureCoordinator
{
    public OmsPrivateAccountRevision? TryCreateAccountRevision(OmsPrivateReplayCut cut, OmsPrivateAccountSubscriptions subscriptions)
    {
        ArgumentNullException.ThrowIfNull(cut); ArgumentNullException.ThrowIfNull(subscriptions);
        lock (replaySync)
        {
            if (!ReferenceEquals(cut.Window.Owner, this) || !Valid(subscriptions)) return null;
            var window = cut.Window; CheckReplay(window);
            if (!ReferenceEquals(cut, window.Cut) || window.Problems.Count != 0 || window.Session != subscriptions.Session ||
                window.Epoch != subscriptions.Epoch || subscriptions.AcknowledgedAtUtc > window.BeganAtUtc ||
                receivedOrdinal != cut.ThroughReceipt || committedOrdinal != cut.ThroughReceipt || pendingReceipts.Count != 0 ||
                window.Entries.Count != cut.ThroughReceipt-window.AfterReceipt ||
                window.Entries.Where((x, i) => x.ReceiptOrdinal != window.AfterReceipt+i+1 ||
                    x.Disposition is not (OmsPrivateCaptureResult.Inserted or OmsPrivateCaptureResult.Duplicate)).Any()) return null;
            return new(this, subscriptions, window.Id, cut.ThroughReceipt, economicGeneration);
        }
    }

    public bool IsAccountRevisionCurrent(OmsPrivateAccountRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        lock (replaySync) return ReferenceEquals(revision.Owner, this) && Valid(revision.Subscriptions) &&
            receivedOrdinal == revision.ThroughReceipt && committedOrdinal == revision.ThroughReceipt && pendingReceipts.Count == 0 &&
            economicGeneration == revision.EconomicGeneration;
    }
}
