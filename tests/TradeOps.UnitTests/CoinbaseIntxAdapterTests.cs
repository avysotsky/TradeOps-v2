using TradeOps.Infrastructure.Exchange.Coinbase;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class CoinbaseIntxAdapterTests
{
    [Fact]
    public void Sign_MatchesFixedVector()
    {
        var signature = CoinbaseIntxSigner.Sign(
            "1700000000",
            "GET",
            "/api/v1/portfolios/portfolio-1/detail",
            string.Empty,
            "dGVzdC1zZWNyZXQ=");

        Assert.Equal(
            "uMAEmb+unjz+9p67tKGOyi4RVvujtPtmm5BozUGO40I=",
            signature);
    }

    [Fact]
    public void Sign_RejectsInvalidBase64Key()
    {
        Assert.Throws<InvalidOperationException>(
            () => CoinbaseIntxSigner.Sign(
                "1700000000",
                "GET",
                "/api/v1/orders",
                string.Empty,
                "not-base64"));
    }

    [Fact]
    public void ClientOrderIdFactory_IsStable()
    {
        var first = CoinbaseIntxClientOrderIdFactory.Create(
            "tradeops-order-123");
        var second = CoinbaseIntxClientOrderIdFactory.Create(
            "tradeops-order-123");

        Assert.Equal(first, second);
        Assert.Equal(32, first.Length);
        Assert.All(
            first,
            character => Assert.True(
                char.IsAsciiHexDigit(character)));
    }

    [Theory]
    [InlineData("BTC-PERP")]
    [InlineData("ETH-PERP")]
    public void ValidateInstrument_AcceptsNativeIntxSymbols(string symbol)
    {
        CoinbaseIntxSandboxExchangeClient.ValidateInstrument(symbol);
    }

    [Theory]
    [InlineData("BTCUSDT")]
    [InlineData("")]
    public void ValidateInstrument_RejectsNonNativeSymbols(string symbol)
    {
        Assert.Throws<ArgumentException>(
            () => CoinbaseIntxSandboxExchangeClient.ValidateInstrument(symbol));
    }
}
