using System.Text.Json.Serialization;

namespace TradeOps.Infrastructure.Exchange.Hyperliquid;

internal sealed class HyperliquidClearinghouseStateDto
{
    [JsonPropertyName("marginSummary")]
    public HyperliquidMarginSummaryDto MarginSummary { get; init; } = new();

    [JsonPropertyName("withdrawable")]
    public string Withdrawable { get; init; } = "0";

    [JsonPropertyName("assetPositions")]
    public List<HyperliquidAssetPositionDto> AssetPositions { get; init; } = [];
}

internal sealed class HyperliquidMarginSummaryDto
{
    [JsonPropertyName("accountValue")]
    public string AccountValue { get; init; } = "0";

    [JsonPropertyName("totalRawUsd")]
    public string TotalRawUsd { get; init; } = "0";
}

internal sealed class HyperliquidAssetPositionDto
{
    [JsonPropertyName("position")]
    public HyperliquidPositionDto Position { get; init; } = new();
}

internal sealed class HyperliquidPositionDto
{
    [JsonPropertyName("coin")]
    public string Coin { get; init; } = string.Empty;

    [JsonPropertyName("entryPx")]
    public string EntryPrice { get; init; } = "0";

    [JsonPropertyName("positionValue")]
    public string PositionValue { get; init; } = "0";

    [JsonPropertyName("szi")]
    public string SignedSize { get; init; } = "0";

    [JsonPropertyName("unrealizedPnl")]
    public string UnrealizedPnl { get; init; } = "0";
}

internal sealed class HyperliquidOrderDto
{
    [JsonPropertyName("coin")]
    public string Coin { get; init; } = string.Empty;

    [JsonPropertyName("side")]
    public string Side { get; init; } = string.Empty;

    [JsonPropertyName("limitPx")]
    public string LimitPrice { get; init; } = "0";

    [JsonPropertyName("sz")]
    public string RemainingSize { get; init; } = "0";

    [JsonPropertyName("origSz")]
    public string OriginalSize { get; init; } = "0";

    [JsonPropertyName("oid")]
    public long OrderId { get; init; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; init; }

    [JsonPropertyName("orderType")]
    public string OrderType { get; init; } = "Limit";

    [JsonPropertyName("cloid")]
    public string? ClientOrderId { get; init; }
}

internal sealed class HyperliquidOrderStatusEnvelopeDto
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("order")]
    public HyperliquidOrderStatusDto? Order { get; init; }
}

internal sealed class HyperliquidOrderStatusDto
{
    [JsonPropertyName("order")]
    public HyperliquidOrderDto Order { get; init; } = new();

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("statusTimestamp")]
    public long StatusTimestamp { get; init; }
}

internal sealed class HyperliquidMetaDto
{
    [JsonPropertyName("universe")]
    public List<HyperliquidAssetMetaDto> Universe { get; init; } = [];
}

internal sealed class HyperliquidAssetMetaDto
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("szDecimals")]
    public int SizeDecimals { get; init; }
}

internal sealed record HyperliquidAssetInfo(
    int AssetId,
    string Name,
    int SizeDecimals);
