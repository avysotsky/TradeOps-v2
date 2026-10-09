using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;

namespace TradeOps.Infrastructure.Exchange;

public sealed class ExchangeCapabilityCatalog(
    ExchangeOptions options) : IExchangeCapabilityCatalog
{
    private static readonly IReadOnlyDictionary<string, ExchangeCapabilityProfile>
        Profiles =
            CreateProfiles()
                .ToDictionary(
                    profile => profile.Provider,
                    StringComparer.OrdinalIgnoreCase);

    public ExchangeCapabilityProfile Current =>
        Get(options.Provider);

    public IReadOnlyCollection<ExchangeCapabilityProfile> All =>
        Profiles.Values
            .OrderBy(profile => profile.Provider, StringComparer.Ordinal)
            .ToArray();

    public ExchangeCapabilityProfile Get(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)
            || !Profiles.TryGetValue(provider.Trim(), out var profile))
        {
            throw new KeyNotFoundException(
                $"Exchange capability profile for provider '{provider}' was not found.");
        }

        return profile;
    }

    private static IEnumerable<ExchangeCapabilityProfile> CreateProfiles()
    {
        yield return Profile(
            ExchangeProviders.Mock,
            "Mock",
            "Mock",
            usesLiveTradingHost: false,
            execution: true,
            privateStream: false,
            "InMemory");

        yield return Profile(
            ExchangeProviders.BybitTestnet,
            "Bybit",
            "Testnet",
            usesLiveTradingHost: false,
            execution: true,
            privateStream: true,
            "REST + WebSocket");

        yield return Profile(
            ExchangeProviders.BinanceFuturesTestnet,
            "Binance USD-M Futures",
            "Testnet",
            usesLiveTradingHost: false,
            execution: true,
            privateStream: true,
            "REST + User Stream");

        yield return Profile(
            ExchangeProviders.HyperliquidTestnet,
            "Hyperliquid",
            "Testnet",
            usesLiveTradingHost: false,
            execution: true,
            privateStream: true,
            "REST + WebSocket");

        yield return Profile(
            ExchangeProviders.MexcFuturesReadOnly,
            "MEXC Futures",
            "LiveReadOnly",
            usesLiveTradingHost: true,
            execution: false,
            privateStream: false,
            "REST");

        yield return Profile(
            ExchangeProviders.DeribitTestnet,
            "Deribit",
            "Testnet",
            usesLiveTradingHost: false,
            execution: true,
            privateStream: true,
            "REST + WebSocket");

        yield return Profile(
            ExchangeProviders.OkxDemo,
            "OKX",
            "Demo",
            usesLiveTradingHost: true,
            execution: true,
            privateStream: true,
            "REST + Demo WebSocket");

        yield return Profile(
            ExchangeProviders.BitgetDemo,
            "Bitget",
            "Demo",
            usesLiveTradingHost: true,
            execution: true,
            privateStream: false,
            "REST + demo header");

        yield return Profile(
            ExchangeProviders.GateFuturesTestnet,
            "Gate Futures",
            "Testnet",
            usesLiveTradingHost: false,
            execution: true,
            privateStream: false,
            "REST");

        yield return Profile(
            ExchangeProviders.KrakenFuturesReadOnly,
            "Kraken Futures",
            "LiveReadOnly",
            usesLiveTradingHost: true,
            execution: false,
            privateStream: false,
            "REST");

        yield return Profile(
            ExchangeProviders.KuCoinFuturesReadOnly,
            "KuCoin Futures",
            "LiveReadOnly",
            usesLiveTradingHost: true,
            execution: false,
            privateStream: false,
            "REST");

        yield return Profile(
            ExchangeProviders.CoinbaseIntxSandbox,
            "Coinbase International Exchange",
            "Sandbox",
            usesLiveTradingHost: false,
            execution: true,
            privateStream: false,
            "REST");

        yield return Profile(
            ExchangeProviders.IbkrPaper,
            "Interactive Brokers",
            "Paper",
            usesLiveTradingHost: false,
            execution: false,
            privateStream: false,
            "Web API / Client Portal Gateway");
    }

    private static ExchangeCapabilityProfile Profile(
        string provider,
        string venue,
        string environment,
        bool usesLiveTradingHost,
        bool execution,
        bool privateStream,
        string transport) =>
        new(
            provider,
            venue,
            environment,
            usesLiveTradingHost,
            SupportsAccountRead: true,
            SupportsPositionRead: true,
            SupportsOpenOrdersRead: true,
            SupportsOrderLookup: true,
            SupportsOrderPlacement: execution,
            SupportsOrderCancellation: execution,
            SupportsPrivateEventStream: privateStream,
            transport);
}
