using System.Globalization;
using System.Text.Json;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Hyperliquid;

internal static class HyperliquidWebSocketMessageParser
{
    public static IReadOnlyCollection<ExchangeOrderUpdate> ParseOrderUpdates(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!string.Equals(
                ReadString(root, "channel"),
                "orderUpdates",
                StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ExchangeOrderUpdate>();
        }

        var result = new List<ExchangeOrderUpdate>();

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("order", out var order)
                || order.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var originalSize = ReadDecimal(order, "origSz");
            var remainingSize = ReadDecimal(order, "sz");
            var filled = Math.Max(0m, originalSize - remainingSize);

            result.Add(new ExchangeOrderUpdate(
                ReadInt64(order, "oid").ToString(CultureInfo.InvariantCulture),
                ReadString(order, "cloid"),
                NormalizeSymbol(ReadString(order, "coin")),
                MapSide(ReadString(order, "side")),
                MapStatus(ReadString(item, "status")),
                originalSize,
                filled,
                null,
                ReadPositiveNullableDecimal(order, "limitPx"),
                ReadTimestamp(item, "statusTimestamp",
                    ReadTimestamp(order, "timestamp"))));
        }

        return result;
    }

    public static IReadOnlyCollection<ExchangeExecutionUpdate> ParseExecutionUpdates(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (!string.Equals(
                ReadString(root, "channel"),
                "userFills",
                StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("fills", out var fills)
            || fills.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ExchangeExecutionUpdate>();
        }

        var result = new List<ExchangeExecutionUpdate>();

        foreach (var fill in fills.EnumerateArray())
        {
            var time = ReadInt64(fill, "time");
            var coin = NormalizeSymbol(ReadString(fill, "coin"));
            var tradeId = ReadInt64(fill, "tid");
            var orderId = ReadInt64(fill, "oid");

            if (time <= 0 || tradeId <= 0 || orderId <= 0)
            {
                continue;
            }

            result.Add(new ExchangeExecutionUpdate(
                orderId.ToString(CultureInfo.InvariantCulture),
                string.Empty,
                $"hl:{time}:{coin}:{tradeId}",
                coin,
                MapSide(ReadString(fill, "side")),
                ReadDecimal(fill, "sz"),
                ReadDecimal(fill, "px"),
                ReadNullableDecimal(fill, "fee"),
                EmptyToNull(ReadString(fill, "feeToken")),
                DateTimeOffset.FromUnixTimeMilliseconds(time)));
        }

        return result;
    }

    private static OrderStatus MapStatus(string status) =>
        status.ToLowerInvariant() switch
        {
            "open" => OrderStatus.Accepted,
            "filled" => OrderStatus.Filled,
            "rejected" => OrderStatus.Rejected,
            "triggered" => OrderStatus.Accepted,
            "canceled" => OrderStatus.Cancelled,
            "margincanceled" => OrderStatus.Cancelled,
            "vaultwithdrawalcanceled" => OrderStatus.Cancelled,
            "openinterestcapcanceled" => OrderStatus.Cancelled,
            "selftradecanceled" => OrderStatus.Cancelled,
            "reduceonlycanceled" => OrderStatus.Cancelled,
            "siblingfilledcanceled" => OrderStatus.Cancelled,
            "delistedcanceled" => OrderStatus.Cancelled,
            "liquidatedcanceled" => OrderStatus.Cancelled,
            "scheduledcancel" => OrderStatus.Cancelled,
            _ when status.EndsWith(
                "Rejected",
                StringComparison.OrdinalIgnoreCase) => OrderStatus.Rejected,
            _ => OrderStatus.Unknown
        };

    private static OrderSide MapSide(string side) =>
        side.ToUpperInvariant() switch
        {
            "B" => OrderSide.Buy,
            "A" => OrderSide.Sell,
            _ => throw new InvalidOperationException(
                $"Unsupported Hyperliquid side '{side}'.")
        };

    private static string NormalizeSymbol(string symbol) =>
        symbol.Trim().ToUpperInvariant();

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
            JsonValueKind.Null => string.Empty,
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
