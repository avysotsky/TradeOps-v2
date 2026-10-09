using TradeOps.Infrastructure.Exchange.Bitget;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class BitgetAdapterTests
{
    [Fact]
    public void Sign_MatchesFixedHmacVector()
    {
        var signature = BitgetSigner.Sign(
            16273667805456,
            "GET",
            "/api/v3/account/fee-rate?category=SPOT&symbol=BTCUSDT",
            string.Empty,
            "test-secret");

        Assert.Equal(
            "TdABQQ9ijAseDaXVnIRMSNNiP8a2N3JfNvKnXjSB3EE=",
            signature);
    }

    [Fact]
    public void Sign_RejectsMissingSecret()
    {
        Assert.Throws<InvalidOperationException>(
            () => BitgetSigner.Sign(
                16273667805456,
                "GET",
                "/api/v3/account/assets",
                string.Empty,
                string.Empty));
    }

    [Fact]
    public void ClientOrderIdFactory_IsStableAndBitgetCompatible()
    {
        var first = BitgetClientOrderIdFactory.Create("tradeops-order-123");
        var second = BitgetClientOrderIdFactory.Create("tradeops-order-123");

        Assert.Equal(first, second);
        Assert.Equal(32, first.Length);
        Assert.All(
            first,
            character => Assert.True(char.IsLetterOrDigit(character)));
    }

    [Theory]
    [InlineData("BTCUSDT")]
    [InlineData("ETHUSDC")]
    public void ValidateSymbol_AcceptsNativeBitgetSymbols(string symbol)
    {
        BitgetDemoExchangeClient.ValidateSymbol(symbol);
    }

    [Theory]
    [InlineData("BTC-USDT")]
    [InlineData("BTC_USDT")]
    [InlineData("")]
    public void ValidateSymbol_RejectsNonNativeSymbols(string symbol)
    {
        Assert.Throws<ArgumentException>(
            () => BitgetDemoExchangeClient.ValidateSymbol(symbol));
    }
}
