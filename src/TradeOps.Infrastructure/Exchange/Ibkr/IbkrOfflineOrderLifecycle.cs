using System.Security.Cryptography;
using System.Text;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Ibkr;

/// <summary>
/// Pure, account-free IBKR order-lifecycle model. This type has no HTTP client,
/// broker connection, trading capability, persistence, or executable order commands.
/// </summary>
public sealed class IbkrOfflineOrderLifecycle
{
    private const int MaxReplySteps = 3;
    private readonly Dictionary<string, Entry> _orders = new(StringComparer.Ordinal);

    public static string CreateDeterministicCoid(string scope, string requestKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestKey);

        // Length prefixes prevent ambiguous concatenations of business keys.
        var payload = $"{scope.Length}:{scope}{requestKey.Length}:{requestKey}";
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return "to-" + Convert.ToHexString(digest)[..24].ToLowerInvariant();
    }

    public IbkrOfflineOrderSnapshot Register(IbkrOfflineOrderIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.ClientOrderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.AccountKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent.Symbol);
        if (intent.Quantity <= 0 || !Enum.IsDefined(intent.Side))
        {
            throw new ArgumentOutOfRangeException(nameof(intent), "Invalid order quantity or side.");
        }

        if (_orders.TryGetValue(intent.ClientOrderId, out var existing))
        {
            if (existing.Snapshot.Intent != intent)
            {
                throw new InvalidOperationException("cOID collision with different order parameters.");
            }

            return existing.Snapshot; // Same business intent cannot schedule another submission.
        }

        var snapshot = new IbkrOfflineOrderSnapshot(intent, IbkrOfflineOrderState.Prepared);
        _orders.Add(intent.ClientOrderId, new Entry(snapshot));
        return snapshot;
    }

    public IbkrOfflineOrderSnapshot Get(string coid) =>
        Find(coid).Snapshot;

    public IReadOnlyList<IbkrOfflineOrderSnapshot> Replay(
        IEnumerable<IbkrOfflineOrderEvent> syntheticEvents)
    {
        ArgumentNullException.ThrowIfNull(syntheticEvents);
        return syntheticEvents.Select(Apply).ToArray();
    }

    public IbkrOfflineOrderSnapshot Apply(IbkrOfflineOrderEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var entry = Find(evt.ClientOrderId);
        var previous = entry.Snapshot;
        var state = previous.State;
        IbkrOfflineOrderSnapshot next;

        switch (evt.Type)
        {
            case IbkrOfflineOrderEventType.PreflightApproved:
                Require(state is IbkrOfflineOrderState.Prepared);
                next = previous with { State = IbkrOfflineOrderState.PreflightApproved };
                break;

            case IbkrOfflineOrderEventType.PreflightRejected:
                Require(state is IbkrOfflineOrderState.Prepared);
                next = previous with { State = IbkrOfflineOrderState.Rejected };
                break;

            case IbkrOfflineOrderEventType.SubmissionStarted:
                // A second submit is unsafe even if no ACK was received.
                Require(state is IbkrOfflineOrderState.PreflightApproved);
                next = previous with { State = IbkrOfflineOrderState.AwaitingAcknowledgement };
                break;

            case IbkrOfflineOrderEventType.ReplyRequired:
            {
                var replyId = Required(evt.ReplyId, nameof(evt.ReplyId));
                if (state is IbkrOfflineOrderState.AwaitingReply
                    && previous.PendingReplyId == replyId)
                {
                    return previous; // Duplicate response, not another reply-chain step.
                }

                Require(state is IbkrOfflineOrderState.AwaitingAcknowledgement);
                if (entry.SeenReplyIds.Contains(replyId) || entry.SeenReplyIds.Count >= MaxReplySteps)
                {
                    throw new InvalidOperationException("IBKR reply chain repeated or exceeded limit.");
                }

                entry.SeenReplyIds.Add(replyId);
                next = previous with
                {
                    State = IbkrOfflineOrderState.AwaitingReply,
                    PendingReplyId = replyId,
                    ReplySteps = entry.SeenReplyIds.Count
                };
                break;
            }

            case IbkrOfflineOrderEventType.ReplyConfirmed:
                if (state is IbkrOfflineOrderState.AwaitingAcknowledgement
                    && previous.LastConfirmedReplyId == evt.ReplyId
                    && !string.IsNullOrEmpty(evt.ReplyId))
                {
                    return previous;
                }

                Require(state is IbkrOfflineOrderState.AwaitingReply
                    && previous.PendingReplyId == Required(evt.ReplyId, nameof(evt.ReplyId)));
                next = previous with
                {
                    State = IbkrOfflineOrderState.AwaitingAcknowledgement,
                    PendingReplyId = null,
                    LastConfirmedReplyId = evt.ReplyId
                };
                break;

            case IbkrOfflineOrderEventType.BrokerAcknowledged:
            {
                var id = Required(evt.BrokerOrderId, nameof(evt.BrokerOrderId));
                if ((state is IbkrOfflineOrderState.Working
                    or IbkrOfflineOrderState.PartiallyFilled
                    or IbkrOfflineOrderState.CancelPending
                    or IbkrOfflineOrderState.Cancelled
                    or IbkrOfflineOrderState.Filled) && previous.BrokerOrderId == id)
                {
                    return previous;
                }

                // ACK after a submit timeout is useful only when a cancel result
                // was not already uncertain. Cancel ambiguity needs reconciliation.
                Require(state is IbkrOfflineOrderState.AwaitingAcknowledgement
                    || (state is IbkrOfflineOrderState.Unknown && previous.BrokerOrderId is null));
                EnsureSameOrderId(previous.BrokerOrderId, id);
                next = previous with { State = IbkrOfflineOrderState.Working, BrokerOrderId = id };
                break;
            }

            case IbkrOfflineOrderEventType.HttpFailure:
            {
                var status = evt.HttpStatus ?? throw new ArgumentException("HTTP status required.");
                if (status is not (401 or 403 or 429) && (status < 500 || status > 599))
                {
                    throw new ArgumentOutOfRangeException(nameof(evt), "Unsupported ambiguous HTTP failure.");
                }

                Require(state is IbkrOfflineOrderState.AwaitingAcknowledgement
                    or IbkrOfflineOrderState.AwaitingReply
                    or IbkrOfflineOrderState.CancelPending);
                next = previous with
                {
                    State = IbkrOfflineOrderState.Unknown,
                    LastHttpStatus = status,
                    PendingReplyId = null
                };
                break;
            }

            case IbkrOfflineOrderEventType.TransportTimedOut:
                Require(state is IbkrOfflineOrderState.AwaitingAcknowledgement
                    or IbkrOfflineOrderState.AwaitingReply
                    or IbkrOfflineOrderState.CancelPending);
                next = previous with
                {
                    State = IbkrOfflineOrderState.Unknown,
                    PendingReplyId = null
                };
                break;

            case IbkrOfflineOrderEventType.Disconnected:
                next = state is IbkrOfflineOrderState.AwaitingAcknowledgement
                    or IbkrOfflineOrderState.AwaitingReply
                    or IbkrOfflineOrderState.Working
                    or IbkrOfflineOrderState.PartiallyFilled
                    or IbkrOfflineOrderState.CancelPending
                    ? previous with { State = IbkrOfflineOrderState.Unknown, PendingReplyId = null }
                    : previous;
                break;

            case IbkrOfflineOrderEventType.ReconciliationNotFound:
                Require(state is IbkrOfflineOrderState.Unknown);
                next = previous; // Not found is NOT permission to place again.
                break;

            case IbkrOfflineOrderEventType.ReconciledWorking:
            case IbkrOfflineOrderEventType.ReconciledCancelled:
            case IbkrOfflineOrderEventType.ReconciledFilled:
            {
                Require(state is IbkrOfflineOrderState.Unknown);
                var id = Required(evt.BrokerOrderId, nameof(evt.BrokerOrderId));
                EnsureSameOrderId(previous.BrokerOrderId, id);
                var filled = CheckedCumulative(evt.CumulativeFilled, previous);
                if (evt.Type is IbkrOfflineOrderEventType.ReconciledFilled
                    && filled != previous.Intent.Quantity)
                {
                    throw new InvalidOperationException("Filled requires full cumulative quantity.");
                }

                next = previous with
                {
                    BrokerOrderId = id,
                    CumulativeFilled = filled,
                    State = filled == previous.Intent.Quantity
                        ? IbkrOfflineOrderState.Filled
                        : evt.Type is IbkrOfflineOrderEventType.ReconciledCancelled
                            ? IbkrOfflineOrderState.Cancelled
                            : filled > 0m
                                ? IbkrOfflineOrderState.PartiallyFilled
                                : IbkrOfflineOrderState.Working
                };
                break;
            }

            case IbkrOfflineOrderEventType.CumulativeFill:
            {
                Require(state is IbkrOfflineOrderState.Working
                    or IbkrOfflineOrderState.PartiallyFilled
                    or IbkrOfflineOrderState.CancelPending
                    or IbkrOfflineOrderState.Cancelled
                    or IbkrOfflineOrderState.Filled);
                var filled = CheckedCumulative(evt.CumulativeFilled, previous);
                next = previous with
                {
                    CumulativeFilled = filled,
                    State = filled == previous.Intent.Quantity
                        ? IbkrOfflineOrderState.Filled
                        : state is IbkrOfflineOrderState.CancelPending
                            or IbkrOfflineOrderState.Cancelled
                            ? state
                            : filled > 0m
                                ? IbkrOfflineOrderState.PartiallyFilled
                                : IbkrOfflineOrderState.Working
                };
                break;
            }

            case IbkrOfflineOrderEventType.CancelRequested:
                if (state is IbkrOfflineOrderState.CancelPending
                    or IbkrOfflineOrderState.Cancelled
                    or IbkrOfflineOrderState.Filled)
                {
                    return previous; // Idempotent local intent; never repeats a broker request.
                }

                Require(state is IbkrOfflineOrderState.Working or IbkrOfflineOrderState.PartiallyFilled);
                next = previous with { State = IbkrOfflineOrderState.CancelPending };
                break;

            case IbkrOfflineOrderEventType.CancelConfirmed:
                if (state is IbkrOfflineOrderState.Filled or IbkrOfflineOrderState.Cancelled)
                {
                    return previous; // Cancel/fill race; do not regress a terminal fill.
                }

                Require(state is IbkrOfflineOrderState.CancelPending);
                next = previous with { State = IbkrOfflineOrderState.Cancelled };
                break;

            case IbkrOfflineOrderEventType.BrokerRejected:
                Require(state is IbkrOfflineOrderState.AwaitingAcknowledgement
                    or IbkrOfflineOrderState.AwaitingReply);
                next = previous with { State = IbkrOfflineOrderState.Rejected, PendingReplyId = null };
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(evt), "Unsupported synthetic event.");
        }

        entry.Snapshot = next;
        return next;
    }

    private Entry Find(string coid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coid);
        return _orders.TryGetValue(coid, out var entry)
            ? entry
            : throw new KeyNotFoundException("Unknown local cOID.");
    }

    private static decimal CheckedCumulative(decimal? value, IbkrOfflineOrderSnapshot previous)
    {
        if (value is not { } cumulative || cumulative < previous.CumulativeFilled
            || cumulative > previous.Intent.Quantity)
        {
            throw new InvalidOperationException("Invalid, decreasing or excessive cumulative fill.");
        }

        return cumulative;
    }

    private static void EnsureSameOrderId(string? existing, string current)
    {
        if (existing is not null && !string.Equals(existing, current, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Broker order identity changed.");
        }
    }

    private static string Required(string? value, string name) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing {name}.");

    private static void Require(bool valid)
    {
        if (!valid)
        {
            throw new InvalidOperationException("Unsafe IBKR offline order transition.");
        }
    }

    private sealed class Entry(IbkrOfflineOrderSnapshot snapshot)
    {
        public IbkrOfflineOrderSnapshot Snapshot { get; set; } = snapshot;
        public HashSet<string> SeenReplyIds { get; } = new(StringComparer.Ordinal);
    }
}

public enum IbkrOfflineOrderState
{
    Prepared,
    PreflightApproved,
    AwaitingAcknowledgement,
    AwaitingReply,
    Working,
    PartiallyFilled,
    CancelPending,
    Unknown,
    Filled,
    Cancelled,
    Rejected
}

public enum IbkrOfflineOrderEventType
{
    PreflightApproved,
    PreflightRejected,
    SubmissionStarted,
    ReplyRequired,
    ReplyConfirmed,
    BrokerAcknowledged,
    HttpFailure,
    TransportTimedOut,
    Disconnected,
    ReconciliationNotFound,
    ReconciledWorking,
    ReconciledCancelled,
    ReconciledFilled,
    CumulativeFill,
    CancelRequested,
    CancelConfirmed,
    BrokerRejected
}

public sealed record IbkrOfflineOrderIntent(
    string ClientOrderId,
    string AccountKey,
    string Symbol,
    OrderSide Side,
    decimal Quantity);

public sealed record IbkrOfflineOrderEvent(
    string ClientOrderId,
    IbkrOfflineOrderEventType Type,
    string? BrokerOrderId = null,
    string? ReplyId = null,
    decimal? CumulativeFilled = null,
    int? HttpStatus = null);

public sealed record IbkrOfflineOrderSnapshot(
    IbkrOfflineOrderIntent Intent,
    IbkrOfflineOrderState State,
    string? BrokerOrderId = null,
    string? PendingReplyId = null,
    string? LastConfirmedReplyId = null,
    decimal CumulativeFilled = 0m,
    int ReplySteps = 0,
    int? LastHttpStatus = null);
