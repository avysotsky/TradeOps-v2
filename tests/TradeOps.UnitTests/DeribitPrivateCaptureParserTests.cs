using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;

public sealed class DeribitPrivateCaptureParserTests
{
    [Theory]
    [InlineData("BTC-PERPETUAL", "BTC", "future", 10, true, "USD")]
    [InlineData("ETH_USDC-25DEC26", "USDC", "future", 2, false, "ETH")]
    [InlineData("BTC-25DEC26-60000-C", "BTC", "option", 1, false, "BTC")]
    public void MetadataControlsUnits_NoFallbackFromAmountToContracts(string instrument, string currency,
        string kind, int amount, bool contracts, string amountCurrency)
    {
        using var document = JsonDocument.Parse(Trade(instrument, currency, kind, amount, contracts ? 1m : null));
        var epoch = Guid.NewGuid();
        var envelope = Assert.Single(DeribitWebSocketMessageParser.ParseCaptureEvents(document.RootElement, Plan(), Scope(), epoch,
            OmsPrivateEventContractTests.Time));
        Assert.Equal(amount, envelope.Facts.NativeAmount);
        Assert.Equal(amountCurrency, envelope.Facts.AmountCurrency);
        Assert.Equal(contracts ? 1m : null, envelope.Facts.Contracts);
        Assert.Equal(epoch, envelope.Observation.StreamEpoch);
        Assert.Equal(OmsPrivateEventContractTests.Time, envelope.Observation.ReceivedAtUtc);
    }

    [Theory]
    [InlineData("trade_id", "null")]
    [InlineData("timestamp", "0")]
    [InlineData("amount", "\"10\"")]
    [InlineData("direction", "\"unknown\"")]
    [InlineData("contracts", "0")]
    [InlineData("price", "\"unknown\"")]
    public void InvalidPrivateEvent_IsARecoveryOutcome_NotSilentlySkipped(string field, string replacement)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Trade())!;
        node["params"]!["data"]![0]![field] = System.Text.Json.Nodes.JsonNode.Parse(replacement);
        using var document = JsonDocument.Parse(node.ToJsonString());
        var failure = Assert.Throws<DeribitCaptureException>(() => DeribitWebSocketMessageParser.ParseCaptureEvents(
            document.RootElement, Plan(), Scope(), Guid.NewGuid(), OmsPrivateEventContractTests.Time));
        Assert.Equal(OmsPrivateStreamProblem.ProtocolViolation, failure.Problem);
    }

    [Fact]
    public void UnknownInstrument_IsRejectedWithoutInferringCurrencyOrAccountFromSymbol()
    {
        using var document = JsonDocument.Parse(Trade("UNKNOWN-PERPETUAL"));
        var failure = Assert.Throws<DeribitCaptureException>(() => DeribitWebSocketMessageParser.ParseCaptureEvents(
            document.RootElement, Plan(), Scope(), Guid.NewGuid(), OmsPrivateEventContractTests.Time));
        Assert.Equal(OmsPrivateStreamProblem.UnsupportedInstrument, failure.Problem);
        Assert.False(failure.Retryable);
    }

    [Fact]
    public void CancelReport_PreservesNativePartialFill_LeavesUnknownWithoutConversion()
    {
        using var document = JsonDocument.Parse("""
{"jsonrpc":"2.0","method":"subscription","params":{"channel":"user.orders.future.BTC.raw","data":{
"instrument_name":"BTC-PERPETUAL","order_id":"o","direction":"buy","amount":100,"contracts":10,
"filled_amount":30,"order_state":"cancelled","price":"market_price","last_update_timestamp":1793793600000}}}
""");
        var item = Assert.Single(DeribitWebSocketMessageParser.ParseCaptureEvents(document.RootElement,
            Plan(), Scope(), Guid.NewGuid(), OmsPrivateEventContractTests.Time));
        Assert.Equal(30m, item.Facts.FilledNativeAmount);
        Assert.Equal(100m, item.Facts.NativeAmount);
        Assert.Null(item.Facts.LeavesNativeAmount);
        Assert.Null(item.Facts.ExchangeTradeId);
        Assert.Null(item.Facts.Price);
        Assert.Equal(OmsPrivatePriceKind.Market, item.Facts.PriceKind);
    }
}
