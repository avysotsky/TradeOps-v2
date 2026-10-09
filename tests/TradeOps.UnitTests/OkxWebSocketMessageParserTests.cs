using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Okx;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class OkxWebSocketMessageParserTests
{
    [Fact]
    public void ParseOrders_MapsPartialFillAndIncrementalExecution()
    {
        const string json = """
        {
          "arg": {
            "channel": "orders",
            "instType": "SWAP"
          },
          "data": [
            {
              "accFillSz": "2",
              "avgPx": "65010.5",
              "cTime": "1760000000000",
              "clOrdId": "0123456789abcdef0123456789abcdef",
              "fillFee": "-0.25",
              "fillFeeCcy": "USDT",
              "fillPx": "65020",
              "fillSz": "1",
              "fillTime": "1760000001000",
              "instId": "BTC-USDT-SWAP",
              "ordId": "123456789",
              "px": "65100",
              "side": "buy",
              "state": "partially_filled",
              "sz": "5",
              "tradeId": "998877",
              "uTime": "1760000001001"
            }
          ]
        }
        """;

        var order = Assert.Single(
            OkxWebSocketMessageParser.ParseOrderUpdates(json));
        var execution = Assert.Single(
            OkxWebSocketMessageParser.ParseExecutionUpdates(json));

        Assert.Equal("123456789", order.ExchangeOrderId);
        Assert.Equal(
            "0123456789abcdef0123456789abcdef",
            order.ClientOrderId);
        Assert.Equal("BTC-USDT-SWAP", order.Symbol);
        Assert.Equal(OrderSide.Buy, order.Side);
        Assert.Equal(
            OrderStatus.PartiallyFilled,
            order.Status);
        Assert.Equal(5m, order.RequestedQuantity);
        Assert.Equal(2m, order.FilledQuantity);
        Assert.Equal(65010.5m, order.AverageFillPrice);
        Assert.Equal(65100m, order.Price);

        Assert.Equal(
            "okx:BTC-USDT-SWAP:998877",
            execution.ExecutionId);
        Assert.Equal(1m, execution.Quantity);
        Assert.Equal(65020m, execution.Price);
        Assert.Equal(0.25m, execution.Fee);
        Assert.Equal("USDT", execution.FeeCurrency);
    }

    [Fact]
    public void ParseExecution_RebateNormalizesToNegativeTradeOpsFee()
    {
        const string json = """
        {
          "arg": {
            "channel": "orders",
            "instType": "FUTURES"
          },
          "data": [
            {
              "clOrdId": "abc",
              "fillFee": "0.15",
              "fillFeeCcy": "USDT",
              "fillPx": "50000",
              "fillSz": "2",
              "fillTime": "1760000002000",
              "instId": "BTC-USDT-261225",
              "ordId": "order-2",
              "side": "sell",
              "tradeId": "trade-2",
              "uTime": "1760000002001"
            }
          ]
        }
        """;

        var execution = Assert.Single(
            OkxWebSocketMessageParser.ParseExecutionUpdates(json));

        Assert.Equal(-0.15m, execution.Fee);
    }

    [Fact]
    public void ParseOrderWithoutTrade_EmitsNoExecution()
    {
        const string json = """
        {
          "arg": {
            "channel": "orders",
            "instType": "SWAP"
          },
          "data": [
            {
              "accFillSz": "0",
              "avgPx": "",
              "cTime": "1760000000000",
              "clOrdId": "abc",
              "fillFee": "",
              "fillFeeCcy": "",
              "fillPx": "",
              "fillSz": "0",
              "fillTime": "",
              "instId": "ETH-USDT-SWAP",
              "ordId": "order-3",
              "px": "2500",
              "side": "sell",
              "state": "live",
              "sz": "3",
              "tradeId": "",
              "uTime": "1760000000001"
            }
          ]
        }
        """;

        var order = Assert.Single(
            OkxWebSocketMessageParser.ParseOrderUpdates(json));

        Assert.Equal(OrderStatus.Accepted, order.Status);
        Assert.Empty(
            OkxWebSocketMessageParser.ParseExecutionUpdates(json));
    }

    [Fact]
    public void Parsers_IgnoreLoginSubscriptionAndOtherChannels()
    {
        const string login = """
        {
          "event": "login",
          "code": "0",
          "msg": "",
          "connId": "abc"
        }
        """;

        const string positions = """
        {
          "arg": {
            "channel": "positions",
            "instType": "SWAP"
          },
          "data": []
        }
        """;

        Assert.Empty(
            OkxWebSocketMessageParser.ParseOrderUpdates(login));
        Assert.Empty(
            OkxWebSocketMessageParser.ParseExecutionUpdates(login));
        Assert.Empty(
            OkxWebSocketMessageParser.ParseOrderUpdates(positions));
        Assert.Empty(
            OkxWebSocketMessageParser.ParseExecutionUpdates(positions));
    }
}
