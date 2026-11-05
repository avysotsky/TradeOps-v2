using System.Text.Json;
using System.Text.Json.Nodes;
using TradeOps.Application.Oms;
using Xunit;
using static TradeOps.UnitTests.DeribitCommandReceiptFixtures;

namespace TradeOps.UnitTests;

public sealed class DeribitCommandReceiptParserTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DirectResponseRetainsOriginalCorrelationAndNativeOrder(bool cancel)
    {
        var command = Command(); if (cancel) command = Cancel(command);
        var r = Receipt(command); var parsed = Parse(r, Response(r));
        Assert.Equal(r, parsed);
        Assert.Equal(r.Order.ExchangeOrderId, parsed.Order.ExchangeOrderId);
    }

    [Theory]
    [InlineData("id")] [InlineData("error")] [InlineData("protocol")]
    [InlineData("missing-filled")] [InlineData("order-id")] [InlineData("post-only")]
    [InlineData("numeric-string")] [InlineData("unknown-type")]
    public void UncorrelatedOrPartialResponsesAreNotBindings(string mode)
    {
        var r = Receipt(Command()); var node = JsonNode.Parse(Response(r).GetRawText())!;
        var order = node["result"]!["order"]!;
        switch (mode)
        {
            case "id": node["id"] = 2; break;
            case "error": node["error"] = new JsonObject { ["code"] = 10000 }; break;
            case "protocol": node["jsonrpc"] = "1.0"; break;
            case "missing-filled": order.AsObject().Remove("filled_amount"); break;
            case "order-id": order["order_id"] = ""; break;
            case "post-only": order.AsObject().Remove("post_only"); break;
            case "numeric-string": order["amount"] = "1"; break;
            default: order["order_type"] = "market"; break;
        }
        using var doc = JsonDocument.Parse(node.ToJsonString());
        Assert.ThrowsAny<ArgumentException>(() => Parse(r, doc.RootElement));
    }

    [Fact]
    public void DuplicateRpcIdCannotBeSilentlyOverwrittenByJsonParser()
    {
        var r = Receipt(Command()); var json = Response(r).GetRawText().Replace("\"id\":1", "\"id\":999,\"id\":1");
        using var doc = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => Parse(r, doc.RootElement));
    }

    [Fact]
    public void InlineTradesAreNotInventedOrPublishedAsExecutionLedger()
    {
        var r = Receipt(Command()); var node = JsonNode.Parse(Response(r).GetRawText())!;
        node["result"]!["trades"] = new JsonArray(new JsonObject { ["trade_id"] = "inline-trade" });
        node["result"]!["order"]!["filled_amount"] = 0.5m;
        using var doc = JsonDocument.Parse(node.ToJsonString()); var parsed = Parse(r, doc.RootElement);
        Assert.Equal(0.5m, parsed.Order.FilledNativeAmount);
        Assert.Equal(OmsJournalEventKind.Order, parsed.Order.Kind); Assert.Null(parsed.Order.ExchangeTradeId);
    }
}
