using System.Text.Json;
using TradeOps.Application.Oms;

namespace TradeOps.Connectors.Deribit;

/// <summary>
/// Pure parsing of a direct response on an already authenticated, native-account-verified
/// order-entry session. No HTTP/WS calls, credentials, database or trading authority.
/// Caller must retain the original request context; recovery query/label results are not responses.
/// </summary>
public static class DeribitJsonRpcCommandReceiptParser
{
    public static DeribitJsonRpcCommandReceipt Parse(OmsJournalCommandRequest command, DeribitPrivateInstrument metadata,
        Guid sessionId, long requestId, string method, DateTimeOffset sentAtUtc, DateTimeOffset receivedAtUtc,
        JsonElement response)
    {
        ArgumentNullException.ThrowIfNull(command); ArgumentNullException.ThrowIfNull(metadata);
        if (response.GetRawText().Length > 262144) throw new ArgumentException("Bounded response required.");
        try
        {
            Unique(response);
            if (response.GetProperty("jsonrpc").GetString() != "2.0" || response.GetProperty("id").GetInt64() != requestId ||
                response.TryGetProperty("error", out _)) throw new ArgumentException("Not a correlated success response.");
            var result = response.GetProperty("result");
            var order = command.Payload.Kind == OmsJournalCommandKind.Place ? result.GetProperty("order") : result;
            var facts = DeribitWebSocketMessageParser.ParsePrivateFacts(order, metadata, false);
            var receipt = new DeribitJsonRpcCommandReceipt(command, 1, sessionId, requestId, method,
                sentAtUtc, receivedAtUtc, metadata.Kind, facts, order.GetProperty("order_type").GetString()!,
                order.GetProperty("time_in_force").GetString()!, order.GetProperty("post_only").GetBoolean(),
                order.GetProperty("reduce_only").GetBoolean());
            receipt.Validate();
            // Inline trades are deliberately not consumed or synthesized into capture/execution facts.
            return receipt;
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new ArgumentException("Invalid native command response."); }
    }

    private static void Unique(JsonElement item, int depth = 0)
    {
        if (depth > 32) throw new ArgumentException("Response nesting exceeded.");
        if (item.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in item.EnumerateObject())
            { if (!names.Add(field.Name)) throw new ArgumentException("Duplicate response field."); Unique(field.Value, depth + 1); }
        }
        else if (item.ValueKind == JsonValueKind.Array) foreach (var child in item.EnumerateArray()) Unique(child, depth + 1);
    }
}
