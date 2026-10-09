using TradeOps.Infrastructure.Exchange.Hyperliquid;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class HyperliquidL1SignerTests
{
    private const string TestPrivateKey =
        "0x0123456789012345678901234567890123456789012345678901234567890123";

    [Fact]
    public void CalculateOrderActionHash_MatchesOfficialSdkVector()
    {
        var hash = HyperliquidL1Signer.CalculateOrderActionHash(
            nonce: 1_677_777_606_040,
            asset: 4,
            isBuy: true,
            price: "1670.1",
            size: "0.0147",
            reduceOnly: false,
            tif: "Ioc");

        Assert.Equal(
            "0fcbeda5ae3c4950a548021552a4fea2226858c4453571bf3f24ba017eac2908",
            Convert.ToHexString(hash).ToLowerInvariant());
    }

    [Fact]
    public void SignOrderTestnet_MatchesOfficialSdkVector()
    {
        var signature = HyperliquidL1Signer.SignOrderTestnet(
            TestPrivateKey,
            nonce: 0,
            asset: 1,
            isBuy: true,
            price: "100",
            size: "100",
            reduceOnly: false,
            tif: "Gtc");

        Assert.Equal(
            "0x82b2ba28e76b3d761093aaded1b1cdad4960b3af30212b343fb2e6cdfa4e3d54",
            signature.R);
        Assert.Equal(
            "0x6b53878fc99d26047f4d7e8c90eb98955a109f44209163f52d8dc4278cbbd9f5",
            signature.S);
        Assert.Equal(27, signature.V);
    }

    [Fact]
    public void SignOrderTestnet_WithCloid_MatchesOfficialSdkVector()
    {
        var signature = HyperliquidL1Signer.SignOrderTestnet(
            TestPrivateKey,
            nonce: 0,
            asset: 1,
            isBuy: true,
            price: "100",
            size: "100",
            reduceOnly: false,
            tif: "Gtc",
            cloid: "0x00000000000000000000000000000001");

        Assert.Equal(
            "0xeba0664bed2676fc4e5a743bf89e5c7501aa6d870bdb9446e122c9466c5cd16d",
            signature.R);
        Assert.Equal(
            "0x7f3e74825c9114bc59086f1eebea2928c190fdfbfde144827cb02b85bbe90988",
            signature.S);
        Assert.Equal(28, signature.V);
    }
}
