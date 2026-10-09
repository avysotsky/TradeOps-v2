using System.Globalization;
using System.Text.Json;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Bybit;

internal static class BybitWebSocketMessageParser
{
    public static IReadOnlyCollection<ExchangeOrderUpdate> ParseOrderUpdates(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var topic = root.TryGetProperty("topic", out var topicElement) ? topicElement.GetString() : null;
        if (topic is null || !topic.StartsWith("order", StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ExchangeOrderUpdate>();
        }

        var fallbackTime = ReadTimestamp(root, "creationTime");
        var result = new List<ExchangeOrderUpdate>();

        foreach (var item in data.EnumerateArray())
        {
            var clientOrderId = ReadString(item, "orderLinkId");
            if (string.IsNullOrWhiteSpace(clientOrderId))
            {
                continue;
            }

            result.Add(new ExchangeOrderUpdate(
                ReadString(item, "orderId"),
                clientOrderId,
                ReadString(item, "symbol"),
                MapSide(ReadString(item, "side")),
                MapStatus(ReadString(item, "orderStatus")),
                ReadDecimal(item, "qty"),
                ReadDecimal(item, "cumExecQty"),
                ReadNullableDecimal(item, "avgPrice"),
                ReadNullableDecimal(item, "price"),
                ReadTimestamp(item, "updatedTime", fallbackTime)));
        }

        return result;
    }

    public static IReadOnlyCollection<ExchangeExecutionUpdate> ParseExecutionUpdates(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var topic = root.TryGetProperty("topic", out var topicElement) ? topicElement.GetString() : null;
        if (topic is null || !topic.StartsWith("execution", StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ExchangeExecutionUpdate>();
        }

        var fallbackTime = ReadTimestamp(root, "creationTime");
        var result = new List<ExchangeExecutionUpdate>();

        foreach (var item in data.EnumerateArray())
        {
            result.Add(new ExchangeExecutionUpdate(
                ReadString(item, "orderId"),
                ReadString(item, "orderLinkId"),
                ReadString(item, "execId"),
                ReadString(item, "symbol"),
                MapSide(ReadString(item, "side")),
                ReadDecimal(item, "execQty"),
                ReadDecimal(item, "execPrice"),
                ReadNullableDecimal(item, "execFee"),
                EmptyToNull(ReadString(item, "feeCurrency")),
                ReadTimestamp(item, "execTime", fallbackTime)));
        }

        return result;
    }

    private static OrderStatus MapStatus(string status) => status switch
    {
        "New" => OrderStatus.Accepted,
        "PartiallyFilled" => OrderStatus.PartiallyFilled,
        "Filled" => OrderStatus.Filled,
        "Cancelled" => OrderStatus.Cancelled,
        "Rejected" => OrderStatus.Rejected,
        "PartiallyFilledCanceled" => OrderStatus.Cancelled,
        "Deactivated" => OrderStatus.Cancelled,
        _ => OrderStatus.Unknown
    };

    private static OrderSide MapSide(string side) => side.Equals("Sell", StringComparison.OrdinalIgnoreCase)
        ? OrderSide.Sell
        : OrderSide.Buy;

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static decimal ReadDecimal(JsonElement element, string name) =>
        ReadNullableDecimal(element, name) ?? 0m;

    private static decimal? ReadNullableDecimal(JsonElement element, string name)
    {
        var raw = ReadString(element, name);
        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static DateTimeOffset ReadTimestamp(JsonElement element, string name, DateTimeOffset? fallback = null)
    {
        if (element.TryGetProperty(name, out var value))
        {
            long milliseconds;
            if ((value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out milliseconds))
                || (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out milliseconds)))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            }
        }

        return fallback ?? DateTimeOffset.UtcNow;
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
