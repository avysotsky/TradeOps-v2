namespace TradeOps.Infrastructure.Exchange.Ibkr;

/// <summary>
/// Offline glue between IBKR-shaped synthetic response fixtures and the
/// lifecycle reducer. This only advances a local model; it never transmits
/// an order, reply, or cancellation to any broker.
/// </summary>
public sealed class IbkrOfflineOrderResponseReplay(IbkrOfflineOrderLifecycle lifecycle)
{
    public IbkrOfflineOrderSnapshot ApplyInitialResponse(string clientOrderId, string json)
    {
        var snapshot = lifecycle.Get(clientOrderId);
        if (snapshot.State is not IbkrOfflineOrderState.AwaitingAcknowledgement
            || snapshot.ReplySteps != 0)
        {
            throw new InvalidOperationException("Initial response is not valid for the current synthetic order state.");
        }

        return ApplyResponse(clientOrderId, json);
    }

    public IbkrOfflineOrderSnapshot ApplyConfirmationResponse(
        string clientOrderId,
        string explicitlyConfirmedReplyId,
        string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(explicitlyConfirmedReplyId);
        var snapshot = lifecycle.Get(clientOrderId);
        if (snapshot.State is not IbkrOfflineOrderState.AwaitingAcknowledgement
            || snapshot.ReplySteps == 0
            || !string.Equals(snapshot.LastConfirmedReplyId,
                explicitlyConfirmedReplyId, StringComparison.Ordinal))
        {
            // The model must first record an explicit ReplyConfirmed event.
            // Merely decoding a broker warning never confirms anything.
            throw new InvalidOperationException("Reply was not explicitly confirmed in the local offline trace.");
        }

        return ApplyResponse(clientOrderId, json);
    }

    private IbkrOfflineOrderSnapshot ApplyResponse(string clientOrderId, string json)
    {
        // Parse fully before changing state. Malformed, conflicting or
        // unsupported payloads leave the state untouched and fail closed.
        var response = IbkrOfflineOrderResponseParser.Parse(json);
        var evt = response.Type switch
        {
            IbkrOfflineOrderResponseType.Acknowledged =>
                new IbkrOfflineOrderEvent(
                    clientOrderId,
                    IbkrOfflineOrderEventType.BrokerAcknowledged,
                    BrokerOrderId: response.BrokerOrderId),
            IbkrOfflineOrderResponseType.ReplyRequired =>
                new IbkrOfflineOrderEvent(
                    clientOrderId,
                    IbkrOfflineOrderEventType.ReplyRequired,
                    ReplyId: response.ReplyId),
            IbkrOfflineOrderResponseType.Rejected =>
                new IbkrOfflineOrderEvent(
                    clientOrderId,
                    IbkrOfflineOrderEventType.BrokerRejected),
            _ => throw new InvalidOperationException("Unsupported offline broker outcome.")
        };

        return lifecycle.Apply(evt);
    }
}
