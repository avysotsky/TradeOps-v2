using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeOps.Infrastructure.Exchange.Mexc;

internal sealed class MexcEnvelope<T>
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("data")]
    public T? Data { get; init; }
}

internal sealed class MexcAssetDto
{
    [JsonPropertyName("currency")]
    public string Currency { get; init; } = string.Empty;

    [JsonPropertyName("availableBalance")]
    public decimal AvailableBalance { get; init; }

    [JsonPropertyName("cashBalance")]
    public decimal CashBalance { get; init; }

    [JsonPropertyName("equity")]
    public decimal Equity { get; init; }

    [JsonPropertyName("unrealized")]
    public decimal Unrealized { get; init; }
}

internal sealed class MexcPositionDto
{
    [JsonPropertyName("positionId")]
    public long PositionId { get; init; }

    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    [JsonPropertyName("positionType")]
    public int PositionType { get; init; }

    [JsonPropertyName("holdVol")]
    public decimal HoldVolume { get; init; }

    [JsonPropertyName("holdAvgPrice")]
    public decimal HoldAveragePrice { get; init; }
}

internal sealed class MexcOrderDto
{
    [JsonPropertyName("orderId")]
    public long OrderId { get; init; }

    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    [JsonPropertyName("price")]
    public decimal Price { get; init; }

    [JsonPropertyName("vol")]
    public decimal Volume { get; init; }

    [JsonPropertyName("side")]
    public int Side { get; init; }

    [JsonPropertyName("orderType")]
    public int OrderType { get; init; }

    [JsonPropertyName("dealAvgPrice")]
    public decimal DealAveragePrice { get; init; }

    [JsonPropertyName("dealVol")]
    public decimal DealVolume { get; init; }

    [JsonPropertyName("state")]
    public int State { get; init; }

    [JsonPropertyName("externalOid")]
    public string ExternalOrderId { get; init; } = string.Empty;

    [JsonPropertyName("createTime")]
    public JsonElement CreateTime { get; init; }

    [JsonPropertyName("updateTime")]
    public JsonElement UpdateTime { get; init; }
}

internal sealed class MexcContractDto
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    [JsonPropertyName("contractSize")]
    public decimal ContractSize { get; init; }

    [JsonPropertyName("apiAllowed")]
    public bool ApiAllowed { get; init; }
}

internal sealed class MexcTickerDto
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    [JsonPropertyName("fairPrice")]
    public decimal FairPrice { get; init; }
}
