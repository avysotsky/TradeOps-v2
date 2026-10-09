using TradeOps.Infrastructure.Exchange.Mexc;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class MexcSignerTests
{
    [Fact]
    public void BuildQueryString_SortsAndEncodesParameters()
    {
        var query = MexcSigner.BuildQueryString(
        [
            new("page_size", "100"),
            new("page_num", "1")
        ]);

        Assert.Equal(
            "page_num=1&page_size=100",
            query);
    }

    [Fact]
    public void Sign_UsesAccessKeyTimestampAndQueryString()
    {
        var signature = MexcSigner.Sign(
            "test-key",
            1_700_000_000_000,
            "page_num=1&page_size=100",
            "test-secret");

        Assert.Equal(
            "d529ea83c0cee3c4962b98a6789f6f2660c327ae9f28bf2fe13ae3392bbb5e09",
            signature);
    }

    [Theory]
    [InlineData("BTCUSDT", "BTC_USDT")]
    [InlineData("ETH_USDC", "ETH_USDC")]
    [InlineData("BTCUSD", "BTC_USD")]
    public void ToMexcSymbol_MapsTradeOpsSymbols(
        string input,
        string expected)
    {
        Assert.Equal(
            expected,
            MexcFuturesReadOnlyExchangeClient.ToMexcSymbol(input));
    }
}
