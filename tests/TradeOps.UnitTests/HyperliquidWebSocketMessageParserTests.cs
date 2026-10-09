using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Hyperliquid;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class HyperliquidWebSocketMessageParserTests
{
    [Fact]
    public void ParseOrderUpdates_MapsCloidAndCumulativeFill()
    {
        const string json = """
        {
          "channel": "orderUpdates",
          "data": [
            {
              "order": {
                "coin": "BTC",
                "side": "B",
                "limitPx": "65000",
                "sz": "0.006",
                "oid": 123456,
                "timestamp": 1760000000000,
                "origSz": "0.010",
                "cloid": "0x11111111111111111111111111111111"
              },
              "status": "open",
              "statusTimestamp": 1760000001000
            }
          ]
        }
        """;

        var update = Assert.Single(
            HyperliquidWebSocketMessageParser.ParseOrderUpdates(json));

        Assert.Equal("123456", update.ExchangeOrderId);
        Assert.Equal("0x11111111111111111111111111111111", update.ClientOrderId);
        Assert.Equal("BTC", update.Symbol);
        Assert.Equal(OrderSide.Buy, update.Side);
        Assert.Equal(OrderStatus.Accepted, update.Status);
        Assert.Equal(0.010m, update.RequestedQuantity);
        Assert.Equal(0.004m, update.FilledQuantity);
        Assert.Equal(65000m, update.Price);
    }

    [Fact]
    public void ParseUserFills_MapsExecutionWithoutClientOrderId()
    {
        const string json = """
        {
          "channel": "userFills",
          "data": {
            "isSnapshot": false,
            "user": "0x1111111111111111111111111111111111111111",
            "fills": [
              {
                "coin": "ETH",
                "px": "2500.25",
                "sz": "0.25",
                "side": "A",
                "time": 1760000002000,
                "startPosition": "1.0",
                "dir": "Close Long",
                "closedPnl": "1.5",
                "hash": "0xabc",
                "oid": 987654,
                "crossed": true,
                "fee": "0.125",
                "tid": 55555,
                "feeToken": "USDC"
              }
            ]
          }
        }
        """;

        var update = Assert.Single(
            HyperliquidWebSocketMessageParser.ParseExecutionUpdates(json));

        Assert.Equal("987654", update.ExchangeOrderId);
        Assert.Equal(string.Empty, update.ClientOrderId);
        Assert.Equal("hl:1760000002000:ETH:55555", update.ExecutionId);
        Assert.Equal("ETH", update.Symbol);
        Assert.Equal(OrderSide.Sell, update.Side);
        Assert.Equal(0.25m, update.Quantity);
        Assert.Equal(2500.25m, update.Price);
        Assert.Equal(0.125m, update.Fee);
        Assert.Equal("USDC", update.FeeCurrency);
    }

    [Fact]
    public void ParseSubscriptionResponse_IgnoresAck()
    {
        const string json = """
        {
          "channel": "subscriptionResponse",
          "data": {
            "method": "subscribe",
            "subscription": {
              "type": "orderUpdates",
              "user": "0x1111111111111111111111111111111111111111"
            }
          }
        }
        """;

        Assert.Empty(
            HyperliquidWebSocketMessageParser.ParseOrderUpdates(json));
        Assert.Empty(
            HyperliquidWebSocketMessageParser.ParseExecutionUpdates(json));
    }
}
