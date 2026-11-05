using System.Text.Json;
using System.Text.Json.Nodes;
using TradeOps.DeribitMainnetEthQualification;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitBoundedOmsPolicyTests
{
    internal static JsonElement Metadata() => JsonSerializer.SerializeToElement(new { instrument_name = "ETH-PERPETUAL", kind = "future",
        instrument_type = "reversed", base_currency = "ETH", quote_currency = "USD", settlement_currency = "ETH", is_active = true,
        min_trade_amount = 1m, contract_size = 1m, tick_size = 0.05m, tick_size_steps = Array.Empty<object>() });
    internal static JsonElement Book() => JsonSerializer.SerializeToElement(new { instrument_name = "ETH-PERPETUAL", state = "open",
        best_bid_price = 2500.03m, best_ask_price = 2500.10m, min_price = 2400m, max_price = 2700m,
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });

    [Fact]
    public void BuyDistanceRoundsAwayFromMarketAndDoesNotTreatSellFloorAsBuyFloor()
    {
        var price = BoundedOmsRiskPolicy.BuyPrice(Metadata(), Book(), DateTimeOffset.UtcNow);
        Assert.Equal(2250m, price); Assert.Equal(0, price % .05m);
        Assert.True(price <= Book().GetProperty("best_bid_price").GetDecimal() * .9m);
        Assert.True(price < Book().GetProperty("min_price").GetDecimal());
    }

    [Theory]
    [InlineData("instrument_name", "BTC-PERPETUAL")] [InlineData("instrument_type", "linear")]
    [InlineData("base_currency", "BTC")] [InlineData("quote_currency", "USDC")]
    [InlineData("settlement_currency", "USD")] [InlineData("kind", "option")]
    public void UnsupportedMetadataCannotSelectAnotherInstrumentOrConvertUnits(string field, string value)
    {
        var node = JsonNode.Parse(Metadata().GetRawText())!; node[field] = value;
        using var doc = JsonDocument.Parse(node.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => BoundedOmsRiskPolicy.BuyPrice(doc.RootElement, Book(), DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("min_trade_amount", 2)] [InlineData("contract_size", 2)] [InlineData("tick_size", 0)]
    public void AmountAndQuantumAreNeverEnlargedToFitVenue(string field, int value)
    {
        var node = JsonNode.Parse(Metadata().GetRawText())!; node[field] = value;
        using var doc = JsonDocument.Parse(node.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => BoundedOmsRiskPolicy.BuyPrice(doc.RootElement, Book(), DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("stale")] [InlineData("future")] [InlineData("locked")] [InlineData("empty")]
    public void InvalidQuotesCannotProduceAnOrder(string mode)
    {
        var node = JsonNode.Parse(Book().GetRawText())!;
        if (mode is "stale" or "future") node["timestamp"] = DateTimeOffset.UtcNow.AddSeconds(mode == "stale" ? -10 : 10).ToUnixTimeMilliseconds();
        else node["best_bid_price"] = mode == "locked" ? 2500.10m : 0;
        using var doc = JsonDocument.Parse(node.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => BoundedOmsRiskPolicy.BuyPrice(Metadata(), doc.RootElement, DateTimeOffset.UtcNow));
    }
}
