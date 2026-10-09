using System.Text.Json.Serialization;

namespace TradeOps.Infrastructure.Exchange.Binance;

internal sealed class BinanceErrorDto
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("msg")]
    public string Message { get; init; } = string.Empty;
}

internal sealed class BinanceAccountDto
{
    [JsonPropertyName("totalWalletBalance")]
    public string TotalWalletBalance { get; init; } = "0";

    [JsonPropertyName("totalMarginBalance")]
    public string TotalMarginBalance { get; init; } = "0";

    [JsonPropertyName("availableBalance")]
    public string AvailableBalance { get; init; } = "0";
}

internal sealed class BinancePositionDto
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    [JsonPropertyName("positionAmt")]
    public string PositionAmount { get; init; } = "0";

    [JsonPropertyName("entryPrice")]
    public string EntryPrice { get; init; } = "0";

    [JsonPropertyName("markPrice")]
    public string MarkPrice { get; init; } = "0";

    [JsonPropertyName("unRealizedProfit")]
    public string UnrealizedProfit { get; init; } = "0";
}

internal sealed class BinanceOrderDto
{
    [JsonPropertyName("orderId")]
    public long OrderId { get; init; }

    [JsonPropertyName("clientOrderId")]
    public string ClientOrderId { get; init; } = string.Empty;

    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    [JsonPropertyName("side")]
    public string Side { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("origQty")]
    public string OriginalQuantity { get; init; } = "0";

    [JsonPropertyName("executedQty")]
    public string ExecutedQuantity { get; init; } = "0";

    [JsonPropertyName("avgPrice")]
    public string AveragePrice { get; init; } = "0";

    [JsonPropertyName("price")]
    public string Price { get; init; } = "0";

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("time")]
    public long Time { get; init; }

    [JsonPropertyName("updateTime")]
    public long UpdateTime { get; init; }
}

internal sealed class BinanceListenKeyDto
{
    [JsonPropertyName("listenKey")]
    public string ListenKey { get; init; } = string.Empty;
}
