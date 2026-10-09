using TradeOps.Infrastructure.Exchange.Hyperliquid;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class HyperliquidPrecisionTests
{
    [Fact]
    public void NormalizeSize_RejectsTooManyDecimals()
    {
        Assert.Throws<ArgumentException>(
            () => HyperliquidExchangeClient.NormalizeSize(
                0.0001m,
                sizeDecimals: 3));
    }

    [Fact]
    public void NormalizeSize_RemovesTrailingZeroes()
    {
        var value = HyperliquidExchangeClient.NormalizeSize(
            1.230m,
            sizeDecimals: 3);

        Assert.Equal("1.23", value);
    }

    [Theory]
    [InlineData("1234.56", 1, "1234.6")]
    [InlineData("0.0012345", 0, "0.001234")]
    [InlineData("123456", 2, "123456")]
    public void NormalizePerpPrice_AppliesHyperliquidRules(
        string raw,
        int sizeDecimals,
        string expected)
    {
        var price = decimal.Parse(
            raw,
            System.Globalization.CultureInfo.InvariantCulture);

        var normalized =
            HyperliquidExchangeClient.NormalizePerpPrice(
                price,
                sizeDecimals);

        Assert.Equal(
            decimal.Parse(
                expected,
                System.Globalization.CultureInfo.InvariantCulture),
            normalized);
    }

    [Fact]
    public void CloidFactory_IsStableAndValid()
    {
        var first = HyperliquidCloidFactory.Create("tradeops-order-123");
        var second = HyperliquidCloidFactory.Create("tradeops-order-123");

        Assert.Equal(first, second);
        Assert.True(HyperliquidCloidFactory.IsValid(first));
        Assert.Equal(34, first.Length);
    }
}
