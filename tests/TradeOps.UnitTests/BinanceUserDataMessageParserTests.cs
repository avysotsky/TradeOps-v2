using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Binance;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class BinanceUserDataMessageParserTests
{
    [Fact]
    public void ParseOrderTradeUpdate_Trade_MapsOrderAndExecution()
    {
        const string json = """
        {
          "e": "ORDER_TRADE_UPDATE",
          "E": 1720000000000,
          "T": 1720000000001,
          "o": {
            "s": "BTCUSDT",
            "c": "tradeops-123",
            "S": "BUY",
            "o": "MARKET",
            "q": "0.010",
            "p": "0",
            "ap": "62000.50",
            "x": "TRADE",
            "X": "PARTIALLY_FILLED",
            "i": 987654321,
            "l": "0.004",
            "z": "0.004",
            "L": "62000.50",
            "N": "USDT",
            "n": "0.0124",
            "T": 1720000000001,
            "t": 456789
          }
        }
        """;

        var order = Assert.Single(
            BinanceUserDataMessageParser.ParseOrderUpdates(json));
        var execution = Assert.Single(
            BinanceUserDataMessageParser.ParseExecutionUpdates(json));

        Assert.Equal("987654321", order.ExchangeOrderId);
        Assert.Equal("tradeops-123", order.ClientOrderId);
        Assert.Equal("BTCUSDT", order.Symbol);
        Assert.Equal(OrderSide.Buy, order.Side);
        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
        Assert.Equal(0.010m, order.RequestedQuantity);
        Assert.Equal(0.004m, order.FilledQuantity);
        Assert.Equal(62000.50m, order.AverageFillPrice);
        Assert.Null(order.Price);

        Assert.Equal("987654321", execution.ExchangeOrderId);
        Assert.Equal("tradeops-123", execution.ClientOrderId);
        Assert.Equal("456789", execution.ExecutionId);
        Assert.Equal(0.004m, execution.Quantity);
        Assert.Equal(62000.50m, execution.Price);
        Assert.Equal(0.0124m, execution.Fee);
        Assert.Equal("USDT", execution.FeeCurrency);
    }

    [Fact]
    public void ParseOrderTradeUpdate_NewOrder_EmitsOrderOnly()
    {
        const string json = """
        {
          "e": "ORDER_TRADE_UPDATE",
          "E": 1720000000000,
          "o": {
            "s": "ETHUSDT",
            "c": "tradeops-456",
            "S": "SELL",
            "q": "0.5",
            "p": "2500",
            "ap": "0",
            "x": "NEW",
            "X": "NEW",
            "i": 12345,
            "l": "0",
            "z": "0",
            "L": "0",
            "N": null,
            "n": "0",
            "T": 1720000000002,
            "t": 0
          }
        }
        """;

        var order = Assert.Single(
            BinanceUserDataMessageParser.ParseOrderUpdates(json));

        Assert.Equal(OrderSide.Sell, order.Side);
        Assert.Equal(OrderStatus.Accepted, order.Status);
        Assert.Equal(2500m, order.Price);
        Assert.Empty(
            BinanceUserDataMessageParser.ParseExecutionUpdates(json));
    }

    [Fact]
    public void ParseAccountUpdate_IgnoresNonOrderEvent()
    {
        const string json = """
        {
          "e": "ACCOUNT_UPDATE",
          "E": 1720000000000,
          "a": { "B": [], "P": [] }
        }
        """;

        Assert.Empty(
            BinanceUserDataMessageParser.ParseOrderUpdates(json));
        Assert.Empty(
            BinanceUserDataMessageParser.ParseExecutionUpdates(json));
    }
}
