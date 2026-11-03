using System.Text.Json;

namespace TradeOps.Infrastructure.Exchange.Ibkr;

/// <summary>
/// Strict, side-effect-free decoder of IBKR-shaped order submission and reply
/// responses. No HTTP calls, no automatic reply confirmation, no live orders.
/// </summary>
public static class IbkrOfflineOrderResponseParser
{
    private const int MaxResponseChars = 32_768;
    private const int MaxIdentifierChars = 128;

    public static IbkrOfflineOrderResponse Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxResponseChars)
        {
            throw Invalid();
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { MaxDepth = 16 });
            var item = document.RootElement;
            if (item.ValueKind == JsonValueKind.Array)
            {
                // Multi-order and mixed success/reply batches are deliberately
                // out of scope. Never silently process only the first item.
                if (item.GetArrayLength() != 1)
                {
                    throw Invalid();
                }

                item = item[0];
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Invalid();
            }

            // Duplicate JSON keys can change security decisions depending on
            // parser implementation. Fail closed rather than taking last value.
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                if (!known.Add(property.Name))
                {
                    throw Invalid();
                }
            }

            var hasError = item.TryGetProperty("error", out var error);
            var hasReplyId = item.TryGetProperty("id", out var replyId);
            var hasMessage = item.TryGetProperty("message", out var messages);
            var hasMessageIds = item.TryGetProperty("messageIds", out var messageIds);
            var hasSuppressed = item.TryGetProperty("isSuppressed", out var suppressed);
            var hasOrderId = item.TryGetProperty("order_id", out var orderId);
            var hasOrderStatus = item.TryGetProperty("order_status", out var orderStatus);
            var hasEncrypted = item.TryGetProperty("encrypt_message", out var encrypted);

            if (hasError)
            {
                if (hasReplyId || hasMessage || hasMessageIds || hasSuppressed
                    || hasOrderId || hasOrderStatus || hasEncrypted
                    || !IsNonemptyString(error))
                {
                    throw Invalid();
                }

                // Do not retain potentially sensitive/revealing broker error
                // text in event traces. Explicit, unambiguous error -> reject.
                return new IbkrOfflineOrderResponse(IbkrOfflineOrderResponseType.Rejected);
            }

            if (hasReplyId || hasMessage || hasMessageIds || hasSuppressed)
            {
                if (hasOrderId || hasOrderStatus || hasEncrypted
                    || !hasReplyId || !hasMessage
                    || !IsSafeIdentifier(replyId) || messages.ValueKind != JsonValueKind.Array
                    || messages.GetArrayLength() == 0)
                {
                    throw Invalid();
                }

                foreach (var message in messages.EnumerateArray())
                {
                    if (!IsNonemptyString(message))
                    {
                        throw Invalid();
                    }
                }

                if (hasSuppressed && suppressed.ValueKind != JsonValueKind.False)
                {
                    throw Invalid();
                }

                if (hasMessageIds)
                {
                    if (messageIds.ValueKind != JsonValueKind.Array)
                    {
                        throw Invalid();
                    }

                    foreach (var code in messageIds.EnumerateArray())
                    {
                        if (!IsSafeIdentifier(code))
                        {
                            throw Invalid();
                        }
                    }
                }

                // Return only count, not human warning text. Confirmation
                // requires explicit, separately audited human/owner intent.
                return new IbkrOfflineOrderResponse(
                    IbkrOfflineOrderResponseType.ReplyRequired,
                    ReplyId: replyId.GetString(),
                    WarningCount: messages.GetArrayLength());
            }

            if (!hasOrderId || !hasOrderStatus
                || !IsSafeIdentifier(orderId)
                || !IsNonemptyString(orderStatus))
            {
                throw Invalid();
            }

            var status = orderStatus.GetString();
            if (status is not ("Submitted" or "PreSubmitted" or "PendingSubmit"))
            {
                // Unknown/filled/cancelled/rejected status cannot be treated
                // as a working-order ACK; reconciliation is required.
                throw Invalid();
            }

            if (hasEncrypted &&
                (encrypted.ValueKind != JsonValueKind.String
                 || encrypted.GetString() != "1"))
            {
                throw Invalid();
            }

            return new IbkrOfflineOrderResponse(
                IbkrOfflineOrderResponseType.Acknowledged,
                BrokerOrderId: orderId.GetString(),
                BrokerStatus: status);
        }
        catch (JsonException)
        {
            throw Invalid();
        }
    }

    private static bool IsNonemptyString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(element.GetString());

    private static bool IsSafeIdentifier(JsonElement element)
    {
        if (!IsNonemptyString(element))
        {
            return false;
        }

        var identifier = element.GetString()!;
        if (identifier.Length > MaxIdentifierChars)
        {
            return false;
        }

        foreach (var character in identifier)
        {
            if (!((character is >= 'a' and <= 'z')
                  || (character is >= 'A' and <= 'Z')
                  || (character is >= '0' and <= '9')
                  || character is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static InvalidOperationException Invalid() =>
        new("Unrecognized or ambiguous IBKR offline order response; no broker action allowed.");
}

public enum IbkrOfflineOrderResponseType
{
    Acknowledged,
    ReplyRequired,
    Rejected
}

public sealed record IbkrOfflineOrderResponse(
    IbkrOfflineOrderResponseType Type,
    string? BrokerOrderId = null,
    string? ReplyId = null,
    string? BrokerStatus = null,
    int WarningCount = 0);
