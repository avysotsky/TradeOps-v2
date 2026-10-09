using System.Security.Cryptography;
using System.Text;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Bybit;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class BybitWebSocketTests
{
    [Fact]
    public void CreateAuthSignature_UsesBybitRealtimePayload()
    {
        const string secret = "test-secret";
        const long expires = 1700000000123;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes($"GET/realtime{expires}")))
            .ToLowerInvariant();

        Assert.Equal(expected, BybitPrivateWebSocketStream.CreateAuthSignature(secret, expires));
    }

    [Fact]
    public void ParseOrderUpdates_MapsLinearOrderState()
    {
        var updates = BybitWebSocketMessageParser.ParseOrderUpdates("""
        {
          "topic":"order.linear",
          "creationTime":1700000000000,
          "data":[{
            "orderId":"exchange-1",
            "orderLinkId":"trd-123",
            "symbol":"BTCUSDT",
            "side":"Buy",
            "orderStatus":"PartiallyFilled",
            "qty":"0.010",
            "cumExecQty":"0.004",
            "avgPrice":"61000.5",
            "price":"62000",
            "updatedTime":"1700000000500"
          }]
        }
        """);

        var update = Assert.Single(updates);
        Assert.Equal("trd-123", update.ClientOrderId);
        Assert.Equal(OrderStatus.PartiallyFilled, update.Status);
        Assert.Equal(0.010m, update.RequestedQuantity);
        Assert.Equal(0.004m, update.FilledQuantity);
        Assert.Equal(61000.5m, update.AverageFillPrice);
    }

    [Fact]
    public void ParseExecutionUpdates_MapsFillEvent()
    {
        var updates = BybitWebSocketMessageParser.ParseExecutionUpdates("""
        {
          "topic":"execution.linear",
          "creationTime":1700000000000,
          "data":[{
            "orderId":"exchange-1",
            "orderLinkId":"trd-123",
            "execId":"exec-1",
            "symbol":"BTCUSDT",
            "side":"Buy",
            "execQty":"0.004",
            "execPrice":"61000.5",
            "execFee":"0.25",
            "feeCurrency":"USDT",
            "execTime":"1700000000400"
          }]
        }
        """);

        var update = Assert.Single(updates);
        Assert.Equal("exec-1", update.ExecutionId);
        Assert.Equal(0.004m, update.Quantity);
        Assert.Equal(61000.5m, update.Price);
        Assert.Equal(0.25m, update.Fee);
        Assert.Equal("USDT", update.FeeCurrency);
    }
}
