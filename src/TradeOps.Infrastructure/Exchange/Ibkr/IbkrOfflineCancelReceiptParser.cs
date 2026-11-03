using System.Globalization;
using System.Text.Json;

namespace TradeOps.Infrastructure.Exchange.Ibkr;

/// <summary>
/// Parses a synthetic IBKR cancellation request receipt. A receipt NEVER
/// proves an order was cancelled. This code cannot send DELETE or other HTTP.
/// </summary>
public static class IbkrOfflineCancelReceiptParser
{
    public static IbkrOfflineCancelReceipt Parse(
        string json, string expectedBrokerOrderId, string expectedAccountKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedBrokerOrderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAccountKey);

        if (string.IsNullOrWhiteSpace(json) || json.Length > 32_768)
        {
            throw Invalid();
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid();
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw Invalid();
                }
            }

            if (root.TryGetProperty("error", out _)
                || !root.TryGetProperty("msg", out var message)
                || message.ValueKind != JsonValueKind.String
                || message.GetString() != "Request was submitted"
                || !root.TryGetProperty("order_id", out var orderId))
            {
                throw Invalid();
            }

            string? observedId = orderId.ValueKind switch
            {
                JsonValueKind.String => orderId.GetString(),
                JsonValueKind.Number when orderId.TryGetInt64(out var number)
                    => number.ToString(CultureInfo.InvariantCulture),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(observedId)
                || !string.Equals(observedId, expectedBrokerOrderId, StringComparison.Ordinal))
            {
                throw Invalid();
            }

            bool accountVerified = false;
            if (root.TryGetProperty("account", out var account))
            {
                if (account.ValueKind == JsonValueKind.String)
                {
                    if (!string.Equals(account.GetString(), expectedAccountKey,
                        StringComparison.Ordinal))
                    {
                        throw Invalid();
                    }

                    accountVerified = true;
                }
                else if (account.ValueKind != JsonValueKind.Null)
                {
                    throw Invalid();
                }
            }

            // IBKR may return account:null (or omit it) when a request was
            // completed immediately. This still does NOT prove cancellation.
            return new IbkrOfflineCancelReceipt(observedId, accountVerified);
        }
        catch (JsonException)
        {
            throw Invalid();
        }
    }

    private static InvalidOperationException Invalid() =>
        new("Invalid or conflicting IBKR offline cancellation receipt; verify order by a separate snapshot.");
}

public sealed record IbkrOfflineCancelReceipt(
    string BrokerOrderId,
    bool AccountVerified);

/// <summary>
/// Validates cancellation receipts for a locally pending synthetic order.
/// Deliberately does not mutate the order state or confirm cancellation.
/// </summary>
public sealed class IbkrOfflineCancelReceiptReplay(IbkrOfflineOrderLifecycle lifecycle)
{
    public IbkrOfflineOrderSnapshot Observe(string clientOrderId, string json)
    {
        var snapshot = lifecycle.Get(clientOrderId);
        if (snapshot.State is not IbkrOfflineOrderState.CancelPending
            || string.IsNullOrWhiteSpace(snapshot.BrokerOrderId))
        {
            throw new InvalidOperationException(
                "Cancel receipt cannot be applied outside a pending known-order cancellation.");
        }

        _ = IbkrOfflineCancelReceiptParser.Parse(
            json, snapshot.BrokerOrderId, snapshot.Intent.AccountKey);
        return lifecycle.Get(clientOrderId); // still CancelPending, never Cancelled
    }
}
