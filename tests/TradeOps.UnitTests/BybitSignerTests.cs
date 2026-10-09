using TradeOps.Infrastructure.Exchange.Bybit;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class BybitSignerTests
{
    [Fact]
    public void CreateHmacSha256Hex_SignsExactPayload()
    {
        var signature = BybitSigner.CreateHmacSha256Hex(
            "test-secret",
            1_658_384_314_791,
            "test-key",
            5_000,
            "category=linear&symbol=BTCUSDT");

        Assert.Equal(
            "acbce6f124ccb7a0f1a447455f5ade0ab2b18de10682b04f40e02d260e338d26",
            signature);
    }
}
