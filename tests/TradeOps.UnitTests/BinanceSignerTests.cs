using TradeOps.Infrastructure.Exchange.Binance;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class BinanceSignerTests
{
    [Fact]
    public void CreateHmacSha256Hex_SignsExactQueryString()
    {
        var signature = BinanceSigner.CreateHmacSha256Hex(
            "test-secret",
            "symbol=BTCUSDT&side=BUY&type=MARKET&quantity=0.001&timestamp=1658384314791");

        Assert.Equal(
            "b1f5ecd846763cb9ed620df38f77a4894e58e317fbd737302b3774e96888eded",
            signature);
    }
}
