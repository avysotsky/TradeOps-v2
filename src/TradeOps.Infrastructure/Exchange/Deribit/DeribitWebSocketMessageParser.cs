using System.Globalization;
using System.Text.Json;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Deribit;

internal static class DeribitWebSocketMessageParser
{
    private const string OrderChannelPrefix = "user.orders.";
    private const string TradeChannelPrefix = "user.trades.";

    public static IReadOnlyCollection<ExchangeOrderUpdate> ParseOrderUpdates(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (!TryGetSubscriptionData(
                document.RootElement,
                OrderChannelPrefix,
                out var data))
        {
            return Array.Empty<ExchangeOrderUpdate>();
        }

        var updates = new List<ExchangeOrderUpdate>();

        foreach (var item in ReadObjects(data))
        {
            var exchangeOrderId = ReadString(item, "order_id");
            var symbol = ReadString(item, "instrument_name");

            if (string.IsNullOrWhiteSpace(exchangeOrderId)
                || string.IsNullOrWhiteSpace(symbol))
            {
                continue;
            }

            var requestedQuantity = ReadRequestedContracts(item);
            var filledQuantity = ReadFilledContracts(item, requestedQuantity);
            var state = ReadString(item, "order_state");

            if (state.Equals("open", StringComparison.OrdinalIgnoreCase)
                && filledQuantity > 0m
                && filledQuantity < requestedQuantity)
            {
                state = "partially_filled";
            }

            updates.Add(
                new ExchangeOrderUpdate(
                    exchangeOrderId,
                    ReadString(item, "label"),
                    symbol,
                    MapSide(ReadString(item, "direction")),
                    MapStatus(state),
                    requestedQuantity,
                    filledQuantity,
                    ReadPositiveNullableDecimal(item, "average_price"),
                    ReadPositiveNullableDecimal(item, "price"),
                    ReadTimestamp(
                        item,
                        "last_update_timestamp",
                        ReadTimestamp(item, "creation_timestamp"))));
        }

        return updates;
    }

    public static IReadOnlyCollection<ExchangeExecutionUpdate> ParseExecutionUpdates(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (!TryGetSubscriptionData(
                document.RootElement,
                TradeChannelPrefix,
                out var data))
        {
            return Array.Empty<ExchangeExecutionUpdate>();
        }

        var updates = new List<ExchangeExecutionUpdate>();

        foreach (var item in ReadObjects(data))
        {
            var executionId = ReadString(item, "trade_id");
            var exchangeOrderId = ReadString(item, "order_id");
            var symbol = ReadString(item, "instrument_name");
            var quantity = ReadDecimal(item, "contracts");

            if (quantity <= 0m)
            {
                quantity = ReadDecimal(item, "amount");
            }

            var price = ReadDecimal(item, "price");

            if (string.IsNullOrWhiteSpace(executionId)
                || string.IsNullOrWhiteSpace(exchangeOrderId)
                || string.IsNullOrWhiteSpace(symbol)
                || quantity <= 0m
                || price <= 0m)
            {
                continue;
            }

            updates.Add(
                new ExchangeExecutionUpdate(
                    exchangeOrderId,
                    ReadString(item, "label"),
                    executionId,
                    symbol,
                    MapSide(ReadString(item, "direction")),
                    quantity,
                    price,
                    ReadNullableDecimal(item, "fee"),
                    EmptyToNull(ReadString(item, "fee_currency")),
                    ReadTimestamp(item, "timestamp")));
        }

        return updates;
    }

    private static bool TryGetSubscriptionData(
        JsonElement root,
        string channelPrefix,
        out JsonElement data)
    {
        data = default;

        if (!string.Equals(
                ReadString(root, "method"),
                "subscription",
                StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("params", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var channel = ReadString(parameters, "channel");

        return channel.StartsWith(channelPrefix, StringComparison.OrdinalIgnoreCase)
            && parameters.TryGetProperty("data", out data)
            && data.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
    }

    private static IReadOnlyCollection<JsonElement> ReadObjects(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object)
        {
            return [data];
        }

        return data
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .ToArray();
    }

    private static decimal ReadRequestedContracts(JsonElement item)
    {
        var contracts = ReadDecimal(item, "contracts");
        return contracts > 0m
            ? contracts
            : ReadDecimal(item, "amount");
    }

    private static decimal ReadFilledContracts(
        JsonElement item,
        decimal requestedQuantity)
    {
        var contracts = ReadDecimal(item, "contracts");
        var amount = ReadDecimal(item, "amount");
        var filledAmount = ReadDecimal(item, "filled_amount");

        if (contracts > 0m && amount > 0m)
        {
            return Math.Min(
                contracts,
                contracts * filledAmount / amount);
        }

        return Math.Min(
            requestedQuantity,
            filledAmount);
    }

    private static OrderStatus MapStatus(string state) =>
        state.ToLowerInvariant() switch
        {
            "open" => OrderStatus.Accepted,
            "partially_filled" => OrderStatus.PartiallyFilled,
            "filled" => OrderStatus.Filled,
            "cancelled" => OrderStatus.Cancelled,
            "canceled" => OrderStatus.Cancelled,
            "rejected" => OrderStatus.Rejected,
            "untriggered" => OrderStatus.Submitted,
            _ => OrderStatus.Unknown
        };

    private static OrderSide MapSide(string side) =>
        side.ToLowerInvariant() switch
        {
            "buy" => OrderSide.Buy,
            "sell" => OrderSide.Sell,
            _ => throw new InvalidOperationException(
                $"Unsupported Deribit side '{side}'.")
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
        var raw = ReadString(element, name);

        return long.TryParse(
                   raw,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out var milliseconds)
               && milliseconds > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : fallback ?? DateTimeOffset.UtcNow;
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
