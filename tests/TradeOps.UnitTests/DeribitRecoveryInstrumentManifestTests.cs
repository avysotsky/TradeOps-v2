using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitRecoveryInstrumentManifestTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static DeribitInstrumentCatalogSnapshot Catalog(DateTimeOffset at) => new(OmsEnvironment.Testnet, "USDC", "future", at,
        [new("BTC_USDC-PERPETUAL", "future", "linear", "BTC", "USDC", "USDC", true, OmsPrivateAmountUnit.BaseCoin, "BTC")]);

    [Fact]
    public void SettlementCurrencyAndOrderAndPositionUnitsRemainDistinctNativeMetadata()
    {
        var result = DeribitRecoveryInstrumentManifest.Create(OmsEnvironment.Testnet, [Catalog(Now)], Now);
        var instrument = Assert.Single(result); Assert.Equal("USDC", instrument.Currency); Assert.Equal("BTC", instrument.AmountCurrency);
        Assert.Equal("BTC", instrument.BaseCurrency); Assert.Equal("USDC", instrument.PositionSizeCurrency);
        Assert.Throws<NotSupportedException>(() => ((IList<DeribitRecoveryInstrument>)result).Clear());
    }

    [Fact]
    public void WrongEnvironmentStaleOrRepeatedCatalogCannotBuildTrustedRecoveryTerms()
    {
        Assert.Throws<ArgumentException>(() => DeribitRecoveryInstrumentManifest.Create(OmsEnvironment.Mainnet, [Catalog(Now)], Now));
        Assert.Throws<ArgumentException>(() => DeribitRecoveryInstrumentManifest.Create(OmsEnvironment.Testnet, [Catalog(Now.AddMinutes(-6))], Now));
        Assert.Throws<ArgumentException>(() => DeribitRecoveryInstrumentManifest.Create(OmsEnvironment.Testnet, [Catalog(Now), Catalog(Now)], Now));
    }
}
