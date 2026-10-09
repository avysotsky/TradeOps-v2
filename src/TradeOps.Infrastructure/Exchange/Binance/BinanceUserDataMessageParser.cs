using System.Globalization;
using System.Text.Json;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Binance;

internal static class BinanceUserDataMessageParser
{
    public static IReadOnlyCollection<ExchangeOrderUpdate> ParseOrderUpdates(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!IsOrderTradeUpdate(root)
            || !root.TryGetProperty("o", out var order)
            || order.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<ExchangeOrderUpdate>();
        }

        var clientOrderId = ReadString(order, "c");
        if (string.IsNullOrWhiteSpace(clientOrderId))
        {
            return Array.Empty<ExchangeOrderUpdate>();
        }

        return
        [
            new ExchangeOrderUpdate(
                ReadInt64(order, "i").ToString(CultureInfo.InvariantCulture),
                clientOrderId,
                ReadString(order, "s"),
                MapSide(ReadString(order, "S")),
                MapStatus(ReadString(order, "X")),
                ReadDecimal(order, "q"),
                ReadDecimal(order, "z"),
                ReadPositiveNullableDecimal(order, "ap"),
                ReadPositiveNullableDecimal(order, "p"),
                ReadTimestamp(order, "T", ReadTimestamp(root, "E")))
        ];
    }

    public static IReadOnlyCollection<ExchangeExecutionUpdate> ParseExecutionUpdates(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!IsOrderTradeUpdate(root)
            || !root.TryGetProperty("o", out var order)
            || order.ValueKind != JsonValueKind.Object
            || !string.Equals(
                ReadString(order, "x"),
                "TRADE",
                StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<ExchangeExecutionUpdate>();
        }

        var clientOrderId = ReadString(order, "c");
        var lastFilledQuantity = ReadDecimal(order, "l");
        var lastFilledPrice = ReadDecimal(order, "L");
        var tradeId = ReadInt64(order, "t");

        if (string.IsNullOrWhiteSpace(clientOrderId)
            || lastFilledQuantity <= 0m
            || lastFilledPrice <= 0m
            || tradeId <= 0)
        {
            return Array.Empty<ExchangeExecutionUpdate>();
        }

        return
        [
            new ExchangeExecutionUpdate(
                ReadInt64(order, "i").ToString(CultureInfo.InvariantCulture),
                clientOrderId,
                tradeId.ToString(CultureInfo.InvariantCulture),
                ReadString(order, "s"),
                MapSide(ReadString(order, "S")),
                lastFilledQuantity,
                lastFilledPrice,
                ReadNullableDecimal(order, "n"),
                EmptyToNull(ReadString(order, "N")),
                ReadTimestamp(order, "T", ReadTimestamp(root, "E")))
        ];
    }

    private static bool IsOrderTradeUpdate(JsonElement root) =>
        string.Equals(
            ReadString(root, "e"),
            "ORDER_TRADE_UPDATE",
            StringComparison.OrdinalIgnoreCase);

    private static OrderStatus MapStatus(string status) =>
        status.ToUpperInvariant() switch
        {
            "NEW" => OrderStatus.Accepted,
            "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
            "FILLED" => OrderStatus.Filled,
            "CANCELED" => OrderStatus.Cancelled,
            "CANCELLED" => OrderStatus.Cancelled,
            "REJECTED" => OrderStatus.Rejected,
            "EXPIRED" => OrderStatus.Cancelled,
            "EXPIRED_IN_MATCH" => OrderStatus.Cancelled,
            _ => OrderStatus.Unknown
        };

    private static OrderSide MapSide(string side) =>
        side.ToUpperInvariant() switch
        {
            "BUY" => OrderSide.Buy,
            "SELL" => OrderSide.Sell,
            _ => throw new InvalidOperationException(
                $"Unsupported Binance side '{side}'.")
        };

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static long ReadInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && long.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed)
            ? parsed
            : 0;
    }

    private static decimal ReadDecimal(JsonElement element, string name) =>
        ReadNullableDecimal(element, name) ?? 0m;

    private static decimal? ReadNullableDecimal(JsonElement element, string name)
    {
        var raw = ReadString(element, name);
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
        var value = ReadNullableDecimal(element, name);
        return value is > 0m ? value : null;
    }

    private static DateTimeOffset ReadTimestamp(
        JsonElement element,
        string name,
        DateTimeOffset? fallback = null)
    {
        var milliseconds = ReadInt64(element, name);
        return milliseconds > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : fallback ?? DateTimeOffset.UtcNow;
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
