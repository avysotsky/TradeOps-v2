using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradeOps.Application.Interfaces;
using TradeOps.Infrastructure.Exchange;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class ExchangeCapabilityCatalogTests
{
    [Fact]
    public void Catalog_CoversEveryDeclaredProviderExactlyOnce()
    {
        var catalog = CreateCatalog(ExchangeProviders.Mock);

        var declared = typeof(ExchangeProviders)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && !field.IsInitOnly)
            .Select(field => (string)field.GetRawConstantValue()!)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var catalogProviders = catalog.All
            .Select(profile => profile.Provider)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(declared, catalogProviders);
        Assert.Equal(
            catalogProviders.Length,
            catalogProviders.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Catalog_LiveReadOnlyProvidersCannotMutate()
    {
        var catalog = CreateCatalog(ExchangeProviders.Mock);

        var liveReadOnly = catalog.All
            .Where(profile =>
                string.Equals(
                    profile.Environment,
                    "LiveReadOnly",
                    StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(liveReadOnly);

        Assert.All(
            liveReadOnly,
            profile =>
            {
                Assert.True(profile.UsesLiveTradingHost);
                Assert.False(profile.SupportsOrderPlacement);
                Assert.False(profile.SupportsOrderCancellation);
                Assert.False(profile.IsExecutionEnabled);
                Assert.False(profile.SupportsPrivateEventStream);
            });
    }

    [Fact]
    public void Catalog_OnlyExpectedProvidersExposePrivateStreams()
    {
        var catalog = CreateCatalog(ExchangeProviders.Mock);

        var actual = catalog.All
            .Where(profile => profile.SupportsPrivateEventStream)
            .Select(profile => profile.Provider)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        var expected = new[]
        {
            ExchangeProviders.BinanceFuturesTestnet,
            ExchangeProviders.BybitTestnet,
            ExchangeProviders.DeribitTestnet,
            ExchangeProviders.HyperliquidTestnet,
            ExchangeProviders.OkxDemo
        }
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Catalog_ExecutionNeverUsesLiveReadOnlyEnvironment()
    {
        var catalog = CreateCatalog(ExchangeProviders.Mock);

        Assert.DoesNotContain(
            catalog.All,
            profile =>
                profile.IsExecutionEnabled
                && string.Equals(
                    profile.Environment,
                    "LiveReadOnly",
                    StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(AllProviders))]
    public void ProviderResolution_MatchesDeclaredPrivateStreamCapability(
        string providerName)
    {
        var configuration = BuildConfiguration(providerName);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTradeOpsExchange(configuration);

        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IExchangeClient>();
        var manager = provider.GetRequiredService<IExchangeConnectionManager>();
        var stream = provider.GetRequiredService<IExchangeEventStream>();
        var catalog = provider.GetRequiredService<IExchangeCapabilityCatalog>();

        Assert.NotNull(client);
        Assert.Same(client, manager);
        Assert.Equal(providerName, catalog.Current.Provider);

        if (catalog.Current.SupportsPrivateEventStream)
        {
            Assert.IsNotType<NullExchangeEventStream>(stream);
        }
        else
        {
            Assert.IsType<NullExchangeEventStream>(stream);
        }
    }

    [Fact]
    public void Current_ReturnsConfiguredProviderProfile()
    {
        var catalog = CreateCatalog(ExchangeProviders.CoinbaseIntxSandbox);

        Assert.Equal(
            ExchangeProviders.CoinbaseIntxSandbox,
            catalog.Current.Provider);
        Assert.Equal("Sandbox", catalog.Current.Environment);
        Assert.True(catalog.Current.IsExecutionEnabled);
        Assert.False(catalog.Current.UsesLiveTradingHost);
    }

    public static IEnumerable<object[]> AllProviders()
    {
        foreach (var field in typeof(ExchangeProviders)
                     .GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(field => field.IsLiteral && !field.IsInitOnly))
        {
            yield return
            [
                (string)field.GetRawConstantValue()!
            ];
        }
    }

    private static IExchangeCapabilityCatalog CreateCatalog(string providerName)
    {
        var configuration = BuildConfiguration(providerName);
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddTradeOpsExchange(configuration);

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IExchangeCapabilityCatalog>();

        return new SnapshotCatalog(
            catalog.Current,
            catalog.All);
    }

    private static IConfiguration BuildConfiguration(string providerName)
    {
        var values = new Dictionary<string, string?>
        {
            ["Exchange:Provider"] = providerName,

            ["Exchange:Hyperliquid:UserAddress"] =
                "0x1111111111111111111111111111111111111111",

            ["Exchange:Bitget:Category"] = "USDT-FUTURES",
            ["Exchange:Bitget:MarginMode"] = "crossed",

            ["Exchange:Gate:Settle"] = "usdt",

            ["Exchange:Okx:ApiKey"] = "demo-key",
            ["Exchange:Okx:ApiSecret"] = "demo-secret",
            ["Exchange:Okx:Passphrase"] = "demo-passphrase",

            ["Exchange:CoinbaseIntx:PortfolioId"] = "portfolio-1"
        };

        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private sealed class SnapshotCatalog(
        TradeOps.Application.Models.ExchangeCapabilityProfile current,
        IReadOnlyCollection<TradeOps.Application.Models.ExchangeCapabilityProfile> all)
        : IExchangeCapabilityCatalog
    {
        public TradeOps.Application.Models.ExchangeCapabilityProfile Current =>
            current;

        public IReadOnlyCollection<TradeOps.Application.Models.ExchangeCapabilityProfile> All =>
            all;

        public TradeOps.Application.Models.ExchangeCapabilityProfile Get(string provider) =>
            all.Single(profile =>
                string.Equals(
                    profile.Provider,
                    provider,
                    StringComparison.OrdinalIgnoreCase));
    }
}
