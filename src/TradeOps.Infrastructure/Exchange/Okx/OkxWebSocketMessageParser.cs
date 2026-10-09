using System.Globalization;
using System.Text.Json;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Okx;

internal static class OkxWebSocketMessageParser
{
    public static IReadOnlyCollection<ExchangeOrderUpdate> ParseOrderUpdates(
        string json)
    {
        using var document = JsonDocument.Parse(json);

        if (!TryGetOrdersData(
                document.RootElement,
                out var data))
        {
            return Array.Empty<ExchangeOrderUpdate>();
        }

        var updates = new List<ExchangeOrderUpdate>();

        foreach (var item in data.EnumerateArray())
        {
            var orderId = ReadString(item, "ordId");
            var symbol = ReadString(item, "instId");

            if (string.IsNullOrWhiteSpace(orderId)
                || string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            var requested = ReadDecimal(item, "sz");
            var filled = Math.Min(
                requested,
                ReadDecimal(item, "accFillSz"));

            var state = ReadString(item, "state");

            if (state.Equals(
                    "live",
                    StringComparison.OrdinalIgnoreCase)
                && filled > 0m
                && filled < requested)
            {
                state = "partially_filled";
            }

            updates.Add(
                new ExchangeOrderUpdate(
                    orderId,
                    ReadString(item, "clOrdId"),
                    symbol,
                    MapSide(ReadString(item, "side")),
                    MapStatus(state),
                    requested,
                    filled,
                    ReadPositiveNullableDecimal(item, "avgPx"),
                    ReadPositiveNullableDecimal(item, "px"),
                    ReadTimestamp(
                        item,
                        "uTime",
                        ReadTimestamp(item, "cTime"))));
        }

        return updates;
    }

    public static IReadOnlyCollection<ExchangeExecutionUpdate>
        ParseExecutionUpdates(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (!TryGetOrdersData(
                document.RootElement,
                out var data))
        {
            return Array.Empty<ExchangeExecutionUpdate>();
        }

        var updates = new List<ExchangeExecutionUpdate>();

        foreach (var item in data.EnumerateArray())
        {
            var tradeId = ReadString(item, "tradeId");
            var orderId = ReadString(item, "ordId");
            var symbol = ReadString(item, "instId");
            var quantity = ReadDecimal(item, "fillSz");
            var price = ReadDecimal(item, "fillPx");

            if (string.IsNullOrWhiteSpace(tradeId)
                || string.IsNullOrWhiteSpace(orderId)
                || string.IsNullOrWhiteSpace(symbol)
                || quantity <= 0m
                || price <= 0m)
            {
                continue;
            }

            var okxFillFee =
                ReadNullableDecimal(item, "fillFee");

            // TradeOps accounting convention is positive=fee paid,
            // negative=rebate. OKX publishes the inverse sign.
            decimal? normalizedFee =
                okxFillFee is null
                    ? null
                    : -okxFillFee.Value;

            updates.Add(
                new ExchangeExecutionUpdate(
                    orderId,
                    ReadString(item, "clOrdId"),
                    $"okx:{symbol}:{tradeId}",
                    symbol,
                    MapSide(ReadString(item, "side")),
                    quantity,
                    price,
                    normalizedFee,
                    EmptyToNull(ReadString(item, "fillFeeCcy")),
                    ReadTimestamp(
                        item,
                        "fillTime",
                        ReadTimestamp(item, "uTime"))));
        }

        return updates;
    }

    private static bool TryGetOrdersData(
        JsonElement root,
        out JsonElement data)
    {
        data = default;

        if (!root.TryGetProperty(
                "arg",
                out var argument)
            || argument.ValueKind != JsonValueKind.Object
            || !string.Equals(
                ReadString(argument, "channel"),
                "orders",
                StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty(
                "data",
                out data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return true;
    }

    private static OrderStatus MapStatus(string state) =>
        state.ToLowerInvariant() switch
        {
            "live" => OrderStatus.Accepted,
            "partially_filled" => OrderStatus.PartiallyFilled,
            "filled" => OrderStatus.Filled,
            "canceled" => OrderStatus.Cancelled,
            "cancelled" => OrderStatus.Cancelled,
            "mmp_canceled" => OrderStatus.Cancelled,
            "order_failed" => OrderStatus.Rejected,
            _ => OrderStatus.Unknown
        };

    private static OrderSide MapSide(string side) =>
        side.ToLowerInvariant() switch
        {
            "buy" => OrderSide.Buy,
            "sell" => OrderSide.Sell,
            _ => throw new InvalidOperationException(
                $"Unsupported OKX side '{side}'.")
        };

    private static string ReadString(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(
                name,
                out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String =>
                value.GetString() ?? string.Empty,
            JsonValueKind.Number =>
                value.GetRawText(),
            _ => string.Empty
        };
    }

    private static decimal ReadDecimal(
        JsonElement element,
        string name) =>
        ReadNullableDecimal(
            element,
            name) ?? 0m;

    private static decimal? ReadNullableDecimal(
        JsonElement element,
        string name)
    {
        var raw = ReadString(
            element,
            name);

        return decimal.TryParse(
            raw,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static decimal? ReadPositiveNullableDecimal(
        JsonElement element,
        string name)
    {
        var value = ReadNullableDecimal(
            element,
            name);

        return value is > 0m
            ? value
            : null;
    }

    private static DateTimeOffset ReadTimestamp(
        JsonElement element,
        string name,
        DateTimeOffset? fallback = null)
    {
        var raw = ReadString(
            element,
            name);

        return long.TryParse(
                   raw,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out var milliseconds)
               && milliseconds > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(
                milliseconds)
            : fallback ?? DateTimeOffset.UtcNow;
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value;
}
