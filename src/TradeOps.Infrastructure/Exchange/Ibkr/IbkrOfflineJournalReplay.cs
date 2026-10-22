using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Ibkr;

/// <summary>
/// Pure validation/replay rules for the opt-in offline PostgreSQL journal.
/// Replaying never makes a network call and cannot place or cancel an order.
/// </summary>
public static class IbkrOfflineJournalReplay
{
    public static IbkrOfflineOrderSnapshot Restore(
        IbkrOfflineOrderIntent intent,
        IReadOnlyList<IbkrOfflineJournalEvent> events)
    {
        ValidateIntent(intent);
        ArgumentNullException.ThrowIfNull(events);
        var lifecycle = new IbkrOfflineOrderLifecycle();
        lifecycle.Register(intent);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in events)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ValidateEvent(intent, entry.EventKey, entry.Event);
            if (!seen.Add(entry.EventKey))
            {
                throw new InvalidOperationException("Duplicate offline journal event key.");
            }
            lifecycle.Apply(entry.Event);
        }

        return lifecycle.Get(intent.ClientOrderId);
    }

    public static void ValidateIntent(IbkrOfflineOrderIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ValidateKey(intent.AccountKey, nameof(intent.AccountKey));
        ValidateKey(intent.ClientOrderId, nameof(intent.ClientOrderId));
        if (string.IsNullOrWhiteSpace(intent.Symbol)
            || intent.Symbol.Length > 80
            || intent.Quantity <= 0m
            || !IsPgQuantity(intent.Quantity)
            || intent.Side is not (OrderSide.Buy or OrderSide.Sell))
        {
            throw new ArgumentException("Invalid offline journal intent.");
        }
    }

    public static void ValidateEvent(
        IbkrOfflineOrderIntent intent,
        string eventKey,
        IbkrOfflineOrderEvent evt)
    {
        ValidateIntent(intent);
        ValidateKey(eventKey, nameof(eventKey));
        ArgumentNullException.ThrowIfNull(evt);
        if (!string.Equals(evt.ClientOrderId, intent.ClientOrderId, StringComparison.Ordinal)
            || !Enum.IsDefined(evt.Type)
            || (evt.BrokerOrderId is not null && !IsKey(evt.BrokerOrderId))
            || (evt.ReplyId is not null && !IsKey(evt.ReplyId))
            || (evt.CumulativeFilled is not null
                && (!IsPgQuantity(evt.CumulativeFilled.Value)
                    || evt.CumulativeFilled.Value < 0m)))
        {
            throw new ArgumentException("Invalid offline journal event identity or value.");
        }

        // An event is canonical: unrelated optional fields cannot be used
        // to bypass idempotency comparisons or hide conflicting evidence.
        var usesBroker = evt.Type is IbkrOfflineOrderEventType.BrokerAcknowledged
            or IbkrOfflineOrderEventType.ReconciledWorking
            or IbkrOfflineOrderEventType.ReconciledCancelled
            or IbkrOfflineOrderEventType.ReconciledFilled;
        var usesReply = evt.Type is IbkrOfflineOrderEventType.ReplyRequired
            or IbkrOfflineOrderEventType.ReplyConfirmed;
        var usesFill = evt.Type is IbkrOfflineOrderEventType.CumulativeFill
            or IbkrOfflineOrderEventType.ReconciledWorking
            or IbkrOfflineOrderEventType.ReconciledCancelled
            or IbkrOfflineOrderEventType.ReconciledFilled;
        var usesHttp = evt.Type is IbkrOfflineOrderEventType.HttpFailure;

        if ((usesBroker != (evt.BrokerOrderId is not null))
            || (usesReply != (evt.ReplyId is not null))
            || (usesFill != (evt.CumulativeFilled is not null))
            || (usesHttp != (evt.HttpStatus is not null)))
        {
            throw new ArgumentException("Noncanonical offline journal event payload.");
        }

        if (evt.CumulativeFilled > intent.Quantity)
        {
            throw new ArgumentException("Offline fill exceeds requested quantity.");
        }
    }

    /// <summary>
    /// Direct PostgreSQL append is reserved for non-evidence-bearing events.
    /// Reconciled states require an atomic snapshot+execution-ID import and
    /// cumulative fills require an atomic execution-ID insertion. An
    /// unverified cancellation acknowledgement cannot finalize an order.
    /// </summary>
    public static void ValidateDirectAppend(IbkrOfflineOrderEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (evt.Type is IbkrOfflineOrderEventType.CumulativeFill
            or IbkrOfflineOrderEventType.ReconciledWorking
            or IbkrOfflineOrderEventType.ReconciledFilled
            or IbkrOfflineOrderEventType.ReconciledCancelled
            or IbkrOfflineOrderEventType.CancelConfirmed)
        {
            throw new InvalidOperationException(
                "Evidence-bound IBKR offline lifecycle events require atomic execution or reconciliation persistence.");
        }
    }

    public static void ValidateKey(string key, string name)
    {
        if (!IsKey(key))
        {
            throw new ArgumentException("Invalid offline journal key.", name);
        }
    }

    private static bool IsKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
        {
            return false;
        }

        return key.All(c =>
            c is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '-' or '_' or '.' or ':');
    }

    private static bool IsPgQuantity(decimal value) =>
        decimal.Round(value, 12) == value
        && value < 10_000_000_000_000_000m;
}

public sealed record IbkrOfflineJournalEvent(
    string EventKey,
    IbkrOfflineOrderEvent Event);

public sealed record IbkrOfflineJournalExecution(
    string ExecutionId,
    string BrokerOrderId,
    decimal Quantity);

public sealed record IbkrOfflineJournalRecovery(
    IbkrOfflineOrderSnapshot Snapshot,
    IReadOnlyList<IbkrOfflineJournalEvent> Events,
    IReadOnlyList<IbkrOfflineJournalExecution> Executions);
