using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Deribit;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitWebSocketMessageParserTests
{
    [Fact]
    public void ParseOrderUpdates_MapsPartialFillUsingContractSemantics()
    {
        const string json = """
        {
          "jsonrpc": "2.0",
          "method": "subscription",
          "params": {
            "channel": "user.orders.future.BTC.raw",
            "data": {
              "order_id": "BTC-123",
              "label": "tradeops-deribit-1",
              "instrument_name": "BTC-PERPETUAL",
              "direction": "buy",
              "order_state": "open",
              "contracts": 10,
              "amount": 100,
              "filled_amount": 40,
              "average_price": 65000.5,
              "price": 65100,
              "creation_timestamp": 1760000000000,
              "last_update_timestamp": 1760000001000
            }
          }
        }
        """;

        var update = Assert.Single(
            DeribitWebSocketMessageParser.ParseOrderUpdates(json));

        Assert.Equal("BTC-123", update.ExchangeOrderId);
        Assert.Equal("tradeops-deribit-1", update.ClientOrderId);
        Assert.Equal("BTC-PERPETUAL", update.Symbol);
        Assert.Equal(OrderSide.Buy, update.Side);
        Assert.Equal(OrderStatus.PartiallyFilled, update.Status);
        Assert.Equal(10m, update.RequestedQuantity);
        Assert.Equal(4m, update.FilledQuantity);
        Assert.Equal(65000.5m, update.AverageFillPrice);
        Assert.Equal(65100m, update.Price);
    }

    [Fact]
    public void ParseExecutionUpdates_MapsTradeArray()
    {
        const string json = """
        {
          "jsonrpc": "2.0",
          "method": "subscription",
          "params": {
            "channel": "user.trades.future.BTC.raw",
            "data": [
              {
                "trade_id": "BTC-987654",
                "order_id": "BTC-123",
                "label": "tradeops-deribit-1",
                "instrument_name": "BTC-PERPETUAL",
                "direction": "sell",
                "contracts": 2,
                "amount": 20,
                "price": 64950.25,
                "fee": 0.00012,
                "fee_currency": "BTC",
                "timestamp": 1760000002000
              }
            ]
          }
        }
        """;

        var update = Assert.Single(
            DeribitWebSocketMessageParser.ParseExecutionUpdates(json));

        Assert.Equal("BTC-123", update.ExchangeOrderId);
        Assert.Equal("tradeops-deribit-1", update.ClientOrderId);
        Assert.Equal("BTC-987654", update.ExecutionId);
        Assert.Equal("BTC-PERPETUAL", update.Symbol);
        Assert.Equal(OrderSide.Sell, update.Side);
        Assert.Equal(2m, update.Quantity);
        Assert.Equal(64950.25m, update.Price);
        Assert.Equal(0.00012m, update.Fee);
        Assert.Equal("BTC", update.FeeCurrency);
    }

    [Fact]
    public void Parsers_IgnoreAcknowledgementsAndUnrelatedChannels()
    {
        const string acknowledgement = """
        {
          "jsonrpc": "2.0",
          "id": 2,
          "result": ["user.orders.future.any.raw"]
        }
        """;

        const string unrelated = """
        {
          "jsonrpc": "2.0",
          "method": "subscription",
          "params": {
            "channel": "ticker.BTC-PERPETUAL.raw",
            "data": {}
          }
        }
        """;

        Assert.Empty(
            DeribitWebSocketMessageParser.ParseOrderUpdates(acknowledgement));
        Assert.Empty(
            DeribitWebSocketMessageParser.ParseExecutionUpdates(acknowledgement));
        Assert.Empty(
            DeribitWebSocketMessageParser.ParseOrderUpdates(unrelated));
        Assert.Empty(
            DeribitWebSocketMessageParser.ParseExecutionUpdates(unrelated));
    }
}
