using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Connectors.Deribit;

internal static partial class DeribitWebSocketMessageParser
{
    internal static IReadOnlyList<OmsPrivateEventEnvelope> ParseCaptureEvents(JsonElement root,
        DeribitPrivateCapturePlan plan, AccountKey scope, Guid epoch, DateTimeOffset received)
    {
        try
        {
            var parameters = root.GetProperty("params");
            var channel = parameters.GetProperty("channel").GetString()!;
            if (!plan.Channels.Contains(channel, StringComparer.Ordinal)) throw new FormatException();
            var execution = channel.StartsWith("user.trades.", StringComparison.Ordinal);
            var data = parameters.GetProperty("data");
            if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > plan.Limits.MaxEventsPerMessage)
                throw new FormatException();
            var records = data.ValueKind == JsonValueKind.Object ? new[] { data } : data.EnumerateArray().ToArray();
            if (records.Length > plan.Limits.MaxEventsPerMessage) throw new FormatException();
            var result = new List<OmsPrivateEventEnvelope>(records.Length);
            foreach (var item in records)
            {
                var instrument = item.GetProperty("instrument_name").GetString()!;
                if (!plan.Instruments.TryGetValue(instrument, out var metadata))
                    throw new DeribitCaptureException(OmsPrivateStreamProblem.UnsupportedInstrument, false);
                var expectedChannel = plan.CapturesAllCurrenciesAndKinds
                    ? execution ? "user.trades.any.any.100ms" : "user.orders.any.any.raw"
                    : execution ? $"user.trades.{metadata.Kind}.{metadata.Currency}.100ms" : $"user.orders.{metadata.Kind}.{metadata.Currency}.raw";
                if (channel != expectedChannel) throw new FormatException();
                var facts = ParsePrivateFacts(item, metadata, execution);
                var envelope = new OmsPrivateEventEnvelope(scope, facts,
                    new(Guid.NewGuid(), epoch, received, OmsPrivateCaptureSource.WebSocket, channel));
                envelope.Validate();
                result.Add(envelope);
            }
            return result;
        }
        catch (DeribitCaptureException) { throw; }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException)
        { throw new DeribitCaptureException(OmsPrivateStreamProblem.ProtocolViolation); }
    }

    // Shared WS/recovery normalization preserves the existing durable v2 encoding.
    internal static OmsPrivateVenueFacts ParsePrivateFacts(JsonElement item, DeribitPrivateInstrument metadata, bool execution)
    {
        if (item.GetProperty("instrument_name").GetString() != metadata.Name) throw new FormatException();
        var price = OptionalNumber(item, "price");
        var priceKind = item.TryGetProperty("price", out var rawPrice) && rawPrice.ValueKind == JsonValueKind.String &&
            rawPrice.GetString() == "market_price" ? OmsPrivatePriceKind.Market : price.HasValue
            ? OmsPrivatePriceKind.Numeric : OmsPrivatePriceKind.Unknown;
        var timestamp = item.GetProperty(execution ? "timestamp" : "last_update_timestamp").GetInt64();
        return new OmsPrivateVenueFacts(execution ? OmsJournalEventKind.Execution : OmsJournalEventKind.Order,
            metadata.Currency, metadata.Name, item.GetProperty("order_id").GetString()!,
            MapSide(item.GetProperty("direction").GetString()!), item.GetProperty("amount").GetDecimal(),
            metadata.AmountUnit, metadata.AmountCurrency, DateTimeOffset.FromUnixTimeMilliseconds(timestamp),
            ExchangeTradeId: execution ? item.GetProperty("trade_id").GetString() : null,
            OrderState: execution ? null : item.GetProperty("order_state").GetString(),
            FilledNativeAmount: execution ? null : OptionalNumber(item, "filled_amount"),
            PriceKind: priceKind, Price: price, AveragePrice: execution ? null : OptionalNumber(item, "average_price"),
            Fee: execution ? OptionalNumber(item, "fee") : null,
            FeeCurrency: execution ? OptionalText(item, "fee_currency") : null,
            Contracts: OptionalNumber(item, "contracts"), ClientLabel: OptionalText(item, "label"));
    }

    private static decimal? OptionalNumber(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (name == "price" && value.ValueKind == JsonValueKind.String && value.GetString() == "market_price") return null;
        return value.GetDecimal(); // no permissive defaults or quantity-unit fallback
    }
    private static string? OptionalText(JsonElement item, string name) =>
        !item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null : EmptyToNull(value.GetString()!);
}
