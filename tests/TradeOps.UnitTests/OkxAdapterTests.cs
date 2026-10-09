using TradeOps.Infrastructure.Exchange.Okx;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class OkxAdapterTests
{
    [Fact]
    public void Sign_MatchesFixedHmacVector()
    {
        var signature = OkxSigner.Sign(
            "2020-12-08T09:08:57.715Z",
            "GET",
            "/api/v5/account/balance?ccy=BTC",
            string.Empty,
            "22582BD0CFF14C41EDBF1AB98506286D");

        Assert.Equal(
            "HiZhvSfMtWJA3uUIVXV3a/bSXNPCWvYFXoGCVS8V4zY=",
            signature);
    }

    [Fact]
    public void WebSocketLoginSignature_MatchesFixedHmacVector()
    {
        var signature = OkxPrivateWebSocketStream.CreateLoginSignature(
            "1538054050",
            "22582BD0CFF14C41EDBF1AB98506286D");

        Assert.Equal(
            "+LdIr8lkkvhr5hoA3g9TMC0+uQJ849ftAcocA/ouu4M=",
            signature);
    }

    [Fact]
    public void ValidateWebSocketConfiguration_AcceptsPortlessDemoEndpoint()
    {
        OkxPrivateWebSocketStream.ValidateConfiguration(
            new OkxOptions
            {
                PrivateWebSocketUrl = "wss://wspap.okx.com/ws/v5/private",
                ApiKey = "demo-key",
                ApiSecret = "demo-secret",
                Passphrase = "demo-passphrase"
            });
    }

    [Fact]
    public void ValidateWebSocketConfiguration_AcceptsLegacy8443DuringTransition()
    {
        OkxPrivateWebSocketStream.ValidateConfiguration(
            new OkxOptions
            {
                PrivateWebSocketUrl = "wss://wspap.okx.com:8443/ws/v5/private",
                ApiKey = "demo-key",
                ApiSecret = "demo-secret",
                Passphrase = "demo-passphrase"
            });
    }

    [Fact]
    public void ClientOrderIdFactory_IsStableAndOkxCompatible()
    {
        var first = OkxClientOrderIdFactory.Create("tradeops-order-123");
        var second = OkxClientOrderIdFactory.Create("tradeops-order-123");

        Assert.Equal(first, second);
        Assert.Equal(32, first.Length);
        Assert.All(first, character => Assert.True(char.IsLetterOrDigit(character)));
    }

    [Theory]
    [InlineData("BTC-USDT-SWAP")]
    [InlineData("BTC-USDT-261225")]
    public void ValidateDerivativeInstrument_AcceptsNativeOkxDerivatives(string symbol)
    {
        OkxDemoExchangeClient.ValidateDerivativeInstrument(symbol);
    }

    [Theory]
    [InlineData("BTC-USDT")]
    [InlineData("BTCUSDT")]
    [InlineData("")]
    public void ValidateDerivativeInstrument_RejectsSpotOrNonNativeSymbols(string symbol)
    {
        Assert.Throws<ArgumentException>(
            () => OkxDemoExchangeClient.ValidateDerivativeInstrument(symbol));
    }
}
