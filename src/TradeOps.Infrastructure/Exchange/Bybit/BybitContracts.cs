using System.Text.Json.Serialization;

namespace TradeOps.Infrastructure.Exchange.Bybit;

internal sealed class BybitEnvelope<T>
{
    [JsonPropertyName("retCode")]
    public int RetCode { get; init; }

    [JsonPropertyName("retMsg")]
    public string RetMsg { get; init; } = string.Empty;

    [JsonPropertyName("result")]
    public T? Result { get; init; }
}

internal sealed class BybitInstrumentResult
{
    [JsonPropertyName("list")]
    public List<BybitInstrumentDto> List { get; init; } = [];
}

internal sealed class BybitInstrumentDto
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("priceFilter")]
    public BybitPriceFilterDto PriceFilter { get; init; } = new();

    [JsonPropertyName("lotSizeFilter")]
    public BybitLotSizeFilterDto LotSizeFilter { get; init; } = new();
}

internal sealed class BybitPriceFilterDto
{
    [JsonPropertyName("minPrice")]
    public string MinPrice { get; init; } = "0";

    [JsonPropertyName("maxPrice")]
    public string MaxPrice { get; init; } = "0";

    [JsonPropertyName("tickSize")]
    public string TickSize { get; init; } = "0";
}

internal sealed class BybitLotSizeFilterDto
{
    [JsonPropertyName("minNotionalValue")]
    public string MinNotionalValue { get; init; } = "0";

    [JsonPropertyName("maxOrderQty")]
    public string MaxOrderQty { get; init; } = "0";

    [JsonPropertyName("maxMktOrderQty")]
    public string MaxMarketOrderQty { get; init; } = "0";

    [JsonPropertyName("minOrderQty")]
    public string MinOrderQty { get; init; } = "0";

    [JsonPropertyName("qtyStep")]
    public string QuantityStep { get; init; } = "0";
}

internal sealed class BybitWalletResult
{
    [JsonPropertyName("list")]
    public List<BybitWalletAccount> List { get; init; } = [];
}

internal sealed class BybitWalletAccount
{
    [JsonPropertyName("totalEquity")]
    public string TotalEquity { get; init; } = "0";

    [JsonPropertyName("totalWalletBalance")]
    public string TotalWalletBalance { get; init; } = "0";

    [JsonPropertyName("totalAvailableBalance")]
    public string TotalAvailableBalance { get; init; } = "0";
}

internal sealed class BybitPositionResult
{
    [JsonPropertyName("list")]
    public List<BybitPositionDto> List { get; init; } = [];
}

internal sealed class BybitPositionDto
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    [JsonPropertyName("side")]
    public string Side { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public string Size { get; init; } = "0";

    [JsonPropertyName("avgPrice")]
    public string AveragePrice { get; init; } = "0";

    [JsonPropertyName("markPrice")]
    public string MarkPrice { get; init; } = "0";

    [JsonPropertyName("unrealisedPnl")]
    public string UnrealizedPnl { get; init; } = "0";
}

internal sealed class BybitOrderListResult
{
    [JsonPropertyName("list")]
    public List<BybitOrderDto> List { get; init; } = [];
}

internal sealed class BybitOrderDto
{
    [JsonPropertyName("orderId")]
    public string OrderId { get; init; } = string.Empty;

    [JsonPropertyName("orderLinkId")]
    public string OrderLinkId { get; init; } = string.Empty;

    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    [JsonPropertyName("side")]
    public string Side { get; init; } = string.Empty;

    [JsonPropertyName("orderType")]
    public string OrderType { get; init; } = string.Empty;

    [JsonPropertyName("qty")]
    public string Quantity { get; init; } = "0";

    [JsonPropertyName("cumExecQty")]
    public string CumulativeExecutedQuantity { get; init; } = "0";

    [JsonPropertyName("avgPrice")]
    public string AveragePrice { get; init; } = string.Empty;

    [JsonPropertyName("price")]
    public string Price { get; init; } = string.Empty;

    [JsonPropertyName("orderStatus")]
    public string OrderStatus { get; init; } = string.Empty;

    [JsonPropertyName("createdTime")]
    public string CreatedTime { get; init; } = string.Empty;

    [JsonPropertyName("updatedTime")]
    public string UpdatedTime { get; init; } = string.Empty;
}

internal sealed class BybitOrderAckResult
{
    [JsonPropertyName("orderId")]
    public string OrderId { get; init; } = string.Empty;

    [JsonPropertyName("orderLinkId")]
    public string OrderLinkId { get; init; } = string.Empty;
}
