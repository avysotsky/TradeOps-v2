using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

/// <summary>
/// Version 1 direct JSON-RPC order-entry response evidence. The trusted sender supplies
/// its original command, session/request correlation and verified native metadata.
/// This is not a label lookup, capture observation, execution, or permission to send.
/// </summary>
public sealed record DeribitJsonRpcCommandReceipt(OmsJournalCommandRequest Command, int AttemptNumber,
    Guid SessionId, long RpcRequestId, string Method, DateTimeOffset SentAtUtc, DateTimeOffset ReceivedAtUtc,
    string InstrumentKind, OmsPrivateVenueFacts Order, string OrderType, string TimeInForce,
    bool PostOnly, bool ReduceOnly)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Command); ArgumentNullException.ThrowIfNull(Order);
        Command.Validate(); Order.Validate();
        var p = Command.Payload;
        OmsPrivateEventCanonical.Utc(SentAtUtc, nameof(SentAtUtc));
        OmsPrivateEventCanonical.Utc(ReceivedAtUtc, nameof(ReceivedAtUtc));
        if (Command.Scope.Venue.Value != "DERIBIT" ||
            Command.Scope.Environment is not (OmsEnvironment.Mainnet or OmsEnvironment.Testnet) ||
            AttemptNumber != 1 || SessionId == Guid.Empty || RpcRequestId <= 0 ||
            ReceivedAtUtc < SentAtUtc || ReceivedAtUtc - SentAtUtc > TimeSpan.FromMinutes(1) ||
            p.RequestedAtUtc > SentAtUtc || Order.VenueTimestampUtc < SentAtUtc.AddSeconds(-5) ||
            Order.VenueTimestampUtc > ReceivedAtUtc.AddSeconds(5))
            throw new ArgumentException("Invalid direct command response correlation/time.");
        // Deliberately narrow v1: inverse futures, native USD, unmodified limit/GTC terms.
        // Contracts/linear/spot/options/trigger/replace/FIX need explicit qualified contracts.
        if (InstrumentKind != "future" || Order.Currency is not ("BTC" or "ETH") ||
            Order.AmountUnit != OmsPrivateAmountUnit.Usd || Order.AmountCurrency != "USD" ||
            p.QuantityUnit != OmsQuantityUnit.QuoteAsset || p.OrderType != TradeOps.Domain.Enums.OrderType.Limit ||
            p.TriggerPrice.HasValue || p.FixClOrdId is not null || p.FixOrigClOrdId is not null ||
            (p.Kind == OmsJournalCommandKind.Place && (p.TargetExchangeOrderId is not null || Command.ExpectedAggregateVersion.Value != 0)) ||
            Order.OrderState is not ("open" or "filled" or "cancelled" or "rejected") ||
            OrderType != "limit" || TimeInForce != "good_til_cancelled" ||
            p.TimeInForce is not ("GTC" or "good_til_cancelled") || PostOnly != p.PostOnly || ReduceOnly != p.ReduceOnly ||
            Order.Kind != OmsJournalEventKind.Order || Order.NativeInstrument != p.Instrument ||
            Order.Side != p.Side || Order.NativeAmount != p.Quantity || Order.PriceKind != OmsPrivatePriceKind.Numeric ||
            Order.Price != p.LimitPrice || Order.FilledNativeAmount is null ||
            (Order.OrderState == "filled" && Order.FilledNativeAmount != Order.NativeAmount) ||
            (Order.OrderState == "rejected" && Order.FilledNativeAmount != 0))
            throw new ArgumentException("Unsupported or mismatched native command response terms.");
        var method = p.Kind switch {
            OmsJournalCommandKind.Place => p.Side == OrderSide.Buy ? "private/buy" : "private/sell",
            OmsJournalCommandKind.Cancel => "private/cancel",
            _ => throw new ArgumentException("Receipt v1 does not support replace.") };
        if (Method != method || (p.Kind == OmsJournalCommandKind.Cancel &&
            (p.TargetExchangeOrderId != Order.ExchangeOrderId || Order.OrderState != "cancelled")))
            throw new ArgumentException("Response does not belong to the addressed command method/target.");
        if (p.TargetClientOrderId is not null && p.TargetClientOrderId != Order.ClientLabel)
            throw new ArgumentException("Echoed label differs from the actual command.");
    }
}

public enum DeribitCommandReceiptWriteResult { Inserted = 1, Duplicate, Collision, NotEligible }
public interface IDeribitCommandReceiptJournal
{
    Task<DeribitCommandReceiptWriteResult> RecordAsync(DeribitJsonRpcCommandReceipt receipt,
        CancellationToken cancellationToken = default);
}

/// <summary>New schema v1; existing journal/private canonical formats are never rewritten.</summary>
public static class DeribitCommandReceiptCanonical
{
    public const int Version = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static (string Json, byte[] Sha256) Encode(DeribitJsonRpcCommandReceipt receipt)
    {
        receipt.Validate();
        using var command = JsonDocument.Parse(OmsJournalCanonical.EncodeCommand(receipt.Command).Json);
        var raw = JsonSerializer.SerializeToElement(new { schemaVersion = Version, command = command.RootElement,
            receipt.AttemptNumber, receipt.SessionId, receipt.RpcRequestId, receipt.Method, receipt.SentAtUtc,
            receipt.ReceivedAtUtc, receipt.InstrumentKind, receipt.Order, receipt.OrderType, receipt.TimeInForce,
            receipt.PostOnly, receipt.ReduceOnly }, Json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(raw, writer);
        var bytes = stream.ToArray();
        if (bytes.Length > 32768) throw new ArgumentException("Receipt exceeds 32 KiB.");
        return (Encoding.UTF8.GetString(bytes), SHA256.HashData(bytes));
    }

    public static DeribitJsonRpcCommandReceipt Decode(string raw, OmsJournalCommandRequest command)
    {
        using var doc = JsonDocument.Parse(raw); var r = doc.RootElement;
        if (r.GetProperty("schemaVersion").GetInt32() != Version) throw new ArgumentException("Unsupported receipt schema.");
        var result = new DeribitJsonRpcCommandReceipt(command, r.GetProperty("attemptNumber").GetInt32(),
            r.GetProperty("sessionId").GetGuid(), r.GetProperty("rpcRequestId").GetInt64(), r.GetProperty("method").GetString()!,
            r.GetProperty("sentAtUtc").GetDateTimeOffset(), r.GetProperty("receivedAtUtc").GetDateTimeOffset(),
            r.GetProperty("instrumentKind").GetString()!, r.GetProperty("order").Deserialize<OmsPrivateVenueFacts>(Json)!,
            r.GetProperty("orderType").GetString()!, r.GetProperty("timeInForce").GetString()!,
            r.GetProperty("postOnly").GetBoolean(), r.GetProperty("reduceOnly").GetBoolean());
        using var encoded = JsonDocument.Parse(Encode(result).Json);
        if (!JsonElement.DeepEquals(r, encoded.RootElement)) throw new ArgumentException("Receipt binding/shape mismatch.");
        return result;
    }

    private static void Write(JsonElement value, Utf8JsonWriter writer)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var field in value.EnumerateObject()) { writer.WritePropertyName(field.Name); Write(field.Value, writer); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(item, writer); writer.WriteEndArray(); }
        else if (value.ValueKind == JsonValueKind.Number)
            writer.WriteRawValue(value.GetDecimal().ToString("G29", CultureInfo.InvariantCulture));
        else value.WriteTo(writer);
    }
}
