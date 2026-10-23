using System.Text.Json;
using TradeOps.Infrastructure.Exchange.Deribit;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitReadOnlyEvidenceParserTests
{
    private static JsonElement Json(string source) =>
        JsonDocument.Parse(source).RootElement.Clone();

    [Fact]
    public void Positions_PreservesInverseFutureUsdSizeAndOptionBaseSize()
    {
        var parsed = DeribitReadOnlyEvidenceParser.ParsePositions(Json("""
            [
              {"instrument_name":"BTC-PERPETUAL","kind":"future",
               "direction":"buy","size":2500,"size_currency":0.037},
              {"instrument_name":"BTC-30OCT26-80000-P","kind":"option",
               "direction":"sell","size":0.5},
              {"instrument_name":"ETH-DEC26-DEC27","kind":"future_combo",
               "direction":"buy","size":25}
            ]
            """));

        Assert.Equal(3, parsed.Count);
        Assert.Equal("future", parsed[0].Kind);
        Assert.Equal(2500m, parsed[0].NativeSize);
        Assert.Equal(0.037m, parsed[0].BaseCurrencySize);
        Assert.Equal("option", parsed[1].Kind);
        Assert.Equal(0.5m, parsed[1].NativeSize);
        Assert.Null(parsed[1].BaseCurrencySize);
        Assert.Equal("future_combo", parsed[2].Kind);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""[{}]""")]
    [InlineData("""[{"instrument_name":"X","kind":"spot","direction":"buy","size":1}]""")]
    [InlineData("""[{"instrument_name":"X","kind":"future","direction":"up","size":1}]""")]
    [InlineData("""[{"instrument_name":"X","kind":"future","direction":"buy","size":-1}]""")]
    [InlineData("""[{"instrument_name":"X","kind":"future","direction":"buy","size":0}]""")]
    [InlineData("""[{"instrument_name":"X","kind":"future","direction":"zero","size":1}]""")]
    [InlineData("""[{"instrument_name":"X","kind":"future","direction":"buy","size":"5"}]""")]
    [InlineData("""[{"instrument_name":"X","kind":"option","direction":"buy","size":1,"size_currency":0.1}]""")]
    [InlineData("""[{"instrument_name":"X","kind":"future","direction":"buy","size":1,"size_currency":-2}]""")]
    [InlineData("""[{"instrument_name":"X","kind":"future","direction":"buy","size":1},{"instrument_name":"X","kind":"option","direction":"sell","size":2}]""")]
    [InlineData("""[{"instrument_name":" X","kind":"future","direction":"buy","size":1}]""")]
    public void Positions_InvalidOrUntrustedEvidence_FailsClosed(string source)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DeribitReadOnlyEvidenceParser.ParsePositions(Json(source)));
    }

    [Fact]
    public void Positions_ValidZeroDirectionRetainedWithoutGuessing()
    {
        var positions = DeribitReadOnlyEvidenceParser.ParsePositions(Json("""
            [{"instrument_name":"ETH-PERPETUAL","kind":"future",
              "direction":"zero","size":0}]
            """));
        Assert.Equal(0m, Assert.Single(positions).NativeSize);
    }

    [Fact]
    public void Trades_PreservesIdsNativeAmountsFeeAndExplicitIncompletePagination()
    {
        var parsed = DeribitReadOnlyEvidenceParser.ParseTradePage(Json("""
            {
              "trades":[
                {"trade_id":"BTC-100","order_id":"BTC-ORDER-1",
                 "instrument_name":"BTC-PERPETUAL",
                 "direction":"buy","amount":100,"price":70000,
                 "fee":-0.00000021,"fee_currency":"BTC",
                 "timestamp":1791000001000,"contracts":10},
                {"trade_id":"BTC-101","order_id":"BTC-ORDER-2",
                 "instrument_name":"BTC-30OCT26-80000-P",
                 "direction":"sell","amount":0.3,"price":0.0200,
                 "fee":0.00003,"fee_currency":"BTC",
                 "timestamp":1791000002000}
              ],
              "has_more":true
            }
            """),"BTC",1791000000000,1791000005000,false);

        Assert.True(parsed.HasMore);
        Assert.False(parsed.IsHistoricalCoverageProven);
        Assert.Equal("BTC", parsed.Currency);
        Assert.False(parsed.Historical);
        Assert.Equal(2, parsed.Trades.Count);
        Assert.Equal(100m, parsed.Trades[0].NativeAmount);
        Assert.Equal(-0.00000021m, parsed.Trades[0].Fee);
        Assert.Equal(10m, parsed.Trades[0].Contracts);
        Assert.Null(parsed.Trades[1].Contracts);
    }

    [Theory]
    [InlineData("""{"trades":[]}""")]
    [InlineData("""{"trades":[],"has_more":null}""")]
    [InlineData("""{"trades":[],"has_more":true}""")]
    [InlineData("""{"trades":{},"has_more":false}""")]
    [InlineData("""{"trades":[{}],"has_more":false}""")]
    [InlineData("""{"trades":[{"trade_id":"1","order_id":"o","instrument_name":"X","direction":"buy","amount":1,"price":2,"fee":0,"fee_currency":"BTC","timestamp":9},{"trade_id":"1","order_id":"o","instrument_name":"X","direction":"buy","amount":1,"price":2,"fee":0,"fee_currency":"BTC","timestamp":9}],"has_more":false}""")]
    [InlineData("""{"trades":[{"trade_id":"1","order_id":"o","instrument_name":"X","direction":"buy","amount":0,"price":2,"fee":0,"fee_currency":"BTC","timestamp":9}],"has_more":false}""")]
    [InlineData("""{"trades":[{"trade_id":"1","order_id":"o","instrument_name":"X","direction":"buy","amount":1,"price":2,"fee":0,"fee_currency":"BTC","timestamp":99}],"has_more":false}""")]
    public void Trades_IncompleteOrConflictingPages_AreRejected(string source)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DeribitReadOnlyEvidenceParser.ParseTradePage(
                Json(source), "BTC", 1, 50, true));
    }

    [Fact]
    public void Trades_EmptyPageIsNotProofOfHistoryCoverage()
    {
        var parsed = DeribitReadOnlyEvidenceParser.ParseTradePage(
            Json("""{"trades":[],"has_more":false}"""),
            "ETH", 100, 200, true);
        Assert.Empty(parsed.Trades);
        Assert.False(parsed.HasMore);
        Assert.False(parsed.IsHistoricalCoverageProven);
    }

    [Theory]
    [InlineData("")]
    [InlineData("btc")]
    [InlineData("../BTC")]
    [InlineData("BTC USDT")]
    public void Trades_RejectsUntrustedCurrencyScope(string currency)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DeribitReadOnlyEvidenceParser.ParseTradePage(
                Json("""{"trades":[],"has_more":false}"""),
                currency, 1, 2, false));
    }
}
