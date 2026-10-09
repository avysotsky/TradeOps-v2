using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradeOps.Application.Interfaces;
using TradeOps.Infrastructure.Exchange.Binance;
using TradeOps.Infrastructure.Exchange.Bitget;
using TradeOps.Infrastructure.Exchange.Bybit;
using TradeOps.Infrastructure.Exchange.Coinbase;
using TradeOps.Infrastructure.Exchange.Deribit;
using TradeOps.Infrastructure.Exchange.Gate;
using TradeOps.Infrastructure.Exchange.Hyperliquid;
using TradeOps.Infrastructure.Exchange.Ibkr;
using TradeOps.Infrastructure.Exchange.Kraken;
using TradeOps.Infrastructure.Exchange.KuCoin;
using TradeOps.Infrastructure.Exchange.Mexc;
using TradeOps.Infrastructure.Exchange.Okx;

namespace TradeOps.Infrastructure.Exchange;

public static class ExchangeServiceRegistration
{
    public static IServiceCollection AddTradeOpsExchange(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var provider = configuration[$"{ExchangeOptions.SectionName}:Provider"]?.Trim();
        provider = string.IsNullOrWhiteSpace(provider) ? ExchangeProviders.Mock : provider;

        var exchangeOptions = new ExchangeOptions { Provider = provider };
        var bitgetOptions = new BitgetOptions
        {
            BaseUrl = configuration[$"{BitgetOptions.SectionName}:BaseUrl"] ?? "https://api.bitget.com",
            ApiKey = configuration[$"{BitgetOptions.SectionName}:ApiKey"] ?? string.Empty,
            ApiSecret = configuration[$"{BitgetOptions.SectionName}:ApiSecret"] ?? string.Empty,
            Passphrase = configuration[$"{BitgetOptions.SectionName}:Passphrase"] ?? string.Empty,
            Category = configuration[$"{BitgetOptions.SectionName}:Category"] ?? "USDT-FUTURES",
            AccountCurrency = configuration[$"{BitgetOptions.SectionName}:AccountCurrency"] ?? "USDT",
            MarginMode = configuration[$"{BitgetOptions.SectionName}:MarginMode"] ?? "crossed",
            HttpTimeoutSeconds = ReadPositiveInt(configuration[$"{BitgetOptions.SectionName}:HttpTimeoutSeconds"], 10)
        };

        var bybitOptions = new BybitOptions
        {
            BaseUrl = configuration[$"{BybitOptions.SectionName}:BaseUrl"] ?? "https://api-testnet.bybit.com",
            PrivateWebSocketUrl = configuration[$"{BybitOptions.SectionName}:PrivateWebSocketUrl"] ?? "wss://stream-testnet.bybit.com/v5/private",
            ApiKey = configuration[$"{BybitOptions.SectionName}:ApiKey"] ?? string.Empty,
            ApiSecret = configuration[$"{BybitOptions.SectionName}:ApiSecret"] ?? string.Empty,
            Category = configuration[$"{BybitOptions.SectionName}:Category"] ?? "linear",
            SettleCoin = configuration[$"{BybitOptions.SectionName}:SettleCoin"] ?? "USDT",
            AccountType = configuration[$"{BybitOptions.SectionName}:AccountType"] ?? "UNIFIED",
            RecvWindowMilliseconds = ReadPositiveInt(configuration[$"{BybitOptions.SectionName}:RecvWindowMilliseconds"], 5_000),
            HttpTimeoutSeconds = ReadPositiveInt(configuration[$"{BybitOptions.SectionName}:HttpTimeoutSeconds"], 10),
            WebSocketPingIntervalSeconds = ReadPositiveInt(configuration[$"{BybitOptions.SectionName}:WebSocketPingIntervalSeconds"], 20)
        };

        var binanceOptions = new BinanceOptions
        {
            BaseUrl = configuration[$"{BinanceOptions.SectionName}:BaseUrl"] ?? "https://testnet.binancefuture.com",
            PrivateWebSocketBaseUrl = configuration[$"{BinanceOptions.SectionName}:PrivateWebSocketBaseUrl"] ?? "wss://stream.binancefuture.com",
            ApiKey = configuration[$"{BinanceOptions.SectionName}:ApiKey"] ?? string.Empty,
            ApiSecret = configuration[$"{BinanceOptions.SectionName}:ApiSecret"] ?? string.Empty,
            SettlementCurrency = configuration[$"{BinanceOptions.SectionName}:SettlementCurrency"] ?? "USDT",
            RecvWindowMilliseconds = ReadPositiveInt(configuration[$"{BinanceOptions.SectionName}:RecvWindowMilliseconds"], 5_000),
            HttpTimeoutSeconds = ReadPositiveInt(configuration[$"{BinanceOptions.SectionName}:HttpTimeoutSeconds"], 10),
            ListenKeyKeepaliveMinutes = ReadPositiveInt(configuration[$"{BinanceOptions.SectionName}:ListenKeyKeepaliveMinutes"], 30)
        };

        var gateOptions = new GateOptions
        {
            BaseUrl = configuration[$"{GateOptions.SectionName}:BaseUrl"] ?? "https://api-testnet.gateapi.io/api/v4",
            ApiKey = configuration[$"{GateOptions.SectionName}:ApiKey"] ?? string.Empty,
            ApiSecret = configuration[$"{GateOptions.SectionName}:ApiSecret"] ?? string.Empty,
            Settle = configuration[$"{GateOptions.SectionName}:Settle"] ?? "usdt",
            HttpTimeoutSeconds = ReadPositiveInt(configuration[$"{GateOptions.SectionName}:HttpTimeoutSeconds"], 10)
        };

        var hyperliquidOptions = new HyperliquidOptions
        {
            BaseUrl = configuration[$"{HyperliquidOptions.SectionName}:BaseUrl"] ?? "https://api.hyperliquid-testnet.xyz",
            WebSocketUrl = configuration[$"{HyperliquidOptions.SectionName}:WebSocketUrl"] ?? "wss://api.hyperliquid-testnet.xyz/ws",
            UserAddress = configuration[$"{HyperliquidOptions.SectionName}:UserAddress"] ?? string.Empty,
            PrivateKey = configuration[$"{HyperliquidOptions.SectionName}:PrivateKey"] ?? string.Empty,
            MarketSlippagePercent = ReadPositiveDecimal(configuration[$"{HyperliquidOptions.SectionName}:MarketSlippagePercent"], 5m),
            HttpTimeoutSeconds = ReadPositiveInt(configuration[$"{HyperliquidOptions.SectionName}:HttpTimeoutSeconds"], 10)
        };

        var coinbaseIntxOptions = new CoinbaseIntxOptions
        {
            BaseUrl = configuration[$"{CoinbaseIntxOptions.SectionName}:BaseUrl"] ?? "https://api-n5e1.coinbase.com/api/v1",
            AccessKey = configuration[$"{CoinbaseIntxOptions.SectionName}:AccessKey"] ?? string.Empty,
            Passphrase = configuration[$"{CoinbaseIntxOptions.SectionName}:Passphrase"] ?? string.Empty,
            SigningKey = configuration[$"{CoinbaseIntxOptions.SectionName}:SigningKey"] ?? string.Empty,
            PortfolioId = configuration[$"{CoinbaseIntxOptions.SectionName}:PortfolioId"] ?? string.Empty,
            AccountCurrency = configuration[$"{CoinbaseIntxOptions.SectionName}:AccountCurrency"] ?? "USDC",
            HttpTimeoutSeconds = ReadPositiveInt(configuration[$"{CoinbaseIntxOptions.SectionName}:HttpTimeoutSeconds"], 10)
        };

        var ibkrOptions = new IbkrOptions
        {
            BaseUrl = configuration[$"{IbkrOptions.SectionName}:BaseUrl"] ?? "https://localhost:5000/v1/api",
            AccountId = configuration[$"{IbkrOptions.SectionName}:AccountId"] ?? string.Empty,
            AccountCurrency = configuration[$"{IbkrOptions.SectionName}:AccountCurrency"] ?? "USD",
            HttpTimeoutSeconds = ReadPositiveInt(configuration[$"{IbkrOptions.SectionName}:HttpTimeoutSeconds"], 15),
            AllowUntrustedLocalhostCertificate = ReadBool(configuration[$"{IbkrOptions.SectionName}:AllowUntrustedLocalhostCertificate"], true)
        };

        var deribitOptions = new DeribitOptions
        {
            BaseUrl = configuration[$"{DeribitOptions.SectionName}:BaseUrl"] ?? "https://test.deribit.com/api/v2",
            WebSocketUrl = configuration[$"{DeribitOptions.SectionName}:WebSocketUrl"] ?? "wss://test.deribit.com/ws/api/v2",
            ClientId = configuration[$"{DeribitOptions.SectionName}:ClientId"] ?? string.Empty,
            ClientSecret = configuration[$"{DeribitOptions.SectionName}:ClientSecret"] ?? string.Empty,
            AccountCurrency = configuration[$"{DeribitOptions.SectionName}:AccountCurrency"] ?? "BTC",
            HttpTimeoutSeconds = ReadPositiveInt(configuration[$"{DeribitOptions.SectionName}:HttpTimeoutSeconds"], 10),
            WebSocketPingIntervalSeconds = ReadPositiveInt(configuration[$"{DeribitOptions.SectionName}:WebSocketPingIntervalSeconds"], 20)
        };

        var okxOptions = new OkxOptions
        {
            BaseUrl = configuration[$"{OkxOptions.SectionName}:BaseUrl"] ?? "https://openapi.okx.com",
            PrivateWebSocketUrl = configuration[$"{OkxOptions.SectionName}:PrivateWebSocketUrl"] ?? "wss://wspap.okx.com/ws/v5/private",
            ApiKey = configuration[$"{OkxOptions.SectionName}:ApiKey"] ?? string.Empty,
            ApiSecret = configuration[$"{OkxOptions.SectionName}:ApiSecret"] ?? string.Empty,
            Passphrase = configuration[$"{OkxOptions.SectionName}:Passphrase"] ?? string.Empty,
            AccountCurrency = configuration[$"{OkxOptions.SectionName}:AccountCurrency"] ?? "USDT",
            TradeMode = configuration[$"{OkxOptions.SectionName}:TradeMode"] ?? "cross",
            HttpTimeoutSeconds = ReadPositiveInt(configuration[$"{OkxOptions.SectionName}:HttpTimeoutSeconds"], 10),
            WebSocketPingIntervalSeconds = ReadPositiveInt(configuration[$"{OkxOptions.SectionName}:WebSocketPingIntervalSeconds"], 20)
        };

        var krakenOptions = new KrakenFuturesOptions
        {
            BaseUrl = configuration[$"{KrakenFuturesOptions.SectionName}:BaseUrl"] ?? "https://futures.kraken.com/derivatives/api/v3",
            ApiKey = configuration[$"{KrakenFuturesOptions.SectionName}:ApiKey"] ?? string.Empty,
            ApiSecret = configuration[$"{KrakenFuturesOptions.SectionName}:ApiSecret"] ?? string.Empty,
            AccountCurrency = configuration[$"{KrakenFuturesOptions.SectionName}:AccountCurrency"] ?? "USD",
            HttpTimeoutSeconds = ReadPositiveInt(configuration[$"{KrakenFuturesOptions.SectionName}:HttpTimeoutSeconds"], 10)
        };

        var kuCoinOptions = new KuCoinFuturesOptions
        {
            BaseUrl = configuration[$"{KuCoinFuturesOptions.SectionName}:BaseUrl"] ?? "https://api-futures.kucoin.com",
            ApiKey = configuration[$"{KuCoinFuturesOptions.SectionName}:ApiKey"] ?? string.Empty,
            ApiSecret = configuration[$"{KuCoinFuturesOptions.SectionName}:ApiSecret"] ?? string.Empty,
            Passphrase = configuration[$"{KuCoinFuturesOptions.SectionName}:Passphrase"] ?? string.Empty,
            KeyVersion = configuration[$"{KuCoinFuturesOptions.SectionName}:KeyVersion"] ?? "2",
            AccountCurrency = configuration[$"{KuCoinFuturesOptions.SectionName}:AccountCurrency"] ?? "USDT",
            HttpTimeoutSeconds = ReadPositiveInt(configuration[$"{KuCoinFuturesOptions.SectionName}:HttpTimeoutSeconds"], 10)
        };

        var mexcOptions = new MexcOptions
        {
            BaseUrl = configuration[$"{MexcOptions.SectionName}:BaseUrl"] ?? "https://contract.mexc.com",
            ApiKey = configuration[$"{MexcOptions.SectionName}:ApiKey"] ?? string.Empty,
            ApiSecret = configuration[$"{MexcOptions.SectionName}:ApiSecret"] ?? string.Empty,
            SettlementCurrency = configuration[$"{MexcOptions.SectionName}:SettlementCurrency"] ?? "USDT",
            RecvWindowSeconds = ReadPositiveInt(configuration[$"{MexcOptions.SectionName}:RecvWindowSeconds"], 10),
            HttpTimeoutSeconds = ReadPositiveInt(configuration[$"{MexcOptions.SectionName}:HttpTimeoutSeconds"], 10)
        };

        services.AddSingleton(exchangeOptions);
        services.AddSingleton<IExchangeCapabilityCatalog, ExchangeCapabilityCatalog>();
        services.AddSingleton(bitgetOptions);
        services.AddSingleton(gateOptions);
        services.AddSingleton(bybitOptions);
        services.AddSingleton(binanceOptions);
        services.AddSingleton(hyperliquidOptions);
        services.AddSingleton(mexcOptions);
        services.AddSingleton(krakenOptions);
        services.AddSingleton(kuCoinOptions);
        services.AddSingleton(deribitOptions);
        services.AddSingleton(coinbaseIntxOptions);
        services.AddSingleton(ibkrOptions);
        services.AddSingleton(okxOptions);
        services.AddHttpClient(BitgetDemoExchangeClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(bitgetOptions.HttpTimeoutSeconds);
        });
        services.AddHttpClient(GateFuturesTestnetExchangeClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(gateOptions.HttpTimeoutSeconds);
        });
        services.AddHttpClient(BybitExchangeClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(bybitOptions.HttpTimeoutSeconds);
        });
        services.AddHttpClient(BinanceFuturesExchangeClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(binanceOptions.HttpTimeoutSeconds);
        });
        services.AddHttpClient(HyperliquidExchangeClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(hyperliquidOptions.HttpTimeoutSeconds);
        });
        services.AddHttpClient(MexcFuturesReadOnlyExchangeClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(mexcOptions.HttpTimeoutSeconds);
        });
        services.AddHttpClient(KrakenFuturesReadOnlyExchangeClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(krakenOptions.HttpTimeoutSeconds);
        });
        services.AddHttpClient(KuCoinFuturesReadOnlyExchangeClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(kuCoinOptions.HttpTimeoutSeconds);
        });
        services.AddHttpClient(DeribitTestnetExchangeClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(deribitOptions.HttpTimeoutSeconds);
        });
        services.AddHttpClient(CoinbaseIntxSandboxExchangeClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(coinbaseIntxOptions.HttpTimeoutSeconds);
        });
        services.AddHttpClient(OkxDemoExchangeClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(okxOptions.HttpTimeoutSeconds);
        });
        services.AddHttpClient(IbkrPaperExchangeClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(ibkrOptions.HttpTimeoutSeconds);
        })
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = new HttpClientHandler();

            if (ibkrOptions.AllowUntrustedLocalhostCertificate)
            {
                handler.ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }

            return handler;
        });

        services.AddSingleton<BitgetDemoExchangeClient>();
        services.AddSingleton<GateFuturesTestnetExchangeClient>();
        services.AddSingleton<MockExchangeClient>();
        services.AddSingleton<BybitExchangeClient>();
        services.AddSingleton<BinanceFuturesExchangeClient>();
        services.AddSingleton<BinanceUserDataStream>();
        services.AddSingleton<HyperliquidExchangeClient>();
        services.AddSingleton<HyperliquidUserDataStream>();
        services.AddSingleton<MexcFuturesReadOnlyExchangeClient>();
        services.AddSingleton<KrakenFuturesReadOnlyExchangeClient>();
        services.AddSingleton<KuCoinFuturesReadOnlyExchangeClient>();
        services.AddSingleton<DeribitTestnetExchangeClient>();
        services.AddSingleton<DeribitPrivateWebSocketStream>();
        services.AddSingleton<CoinbaseIntxSandboxExchangeClient>();
        services.AddSingleton<OkxDemoExchangeClient>();
        services.AddSingleton<OkxPrivateWebSocketStream>();
        services.AddSingleton<IbkrInstrumentResolver>();
        services.AddSingleton<IbkrPaperExchangeClient>();
        services.AddSingleton<NullExchangeEventStream>();
        services.AddSingleton<BybitPrivateWebSocketStream>();

        if (string.Equals(provider, ExchangeProviders.Mock, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<MockExchangeClient>());
            services.AddSingleton<IExchangeConnectionManager>(sp => sp.GetRequiredService<MockExchangeClient>());
            services.AddSingleton<IExchangeEventStream>(sp => sp.GetRequiredService<NullExchangeEventStream>());
            return services;
        }

        if (string.Equals(provider, ExchangeProviders.BybitTestnet, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<BybitExchangeClient>());
            services.AddSingleton<IExchangeConnectionManager>(sp => sp.GetRequiredService<BybitExchangeClient>());
            services.AddSingleton<IExchangeEventStream>(sp => sp.GetRequiredService<BybitPrivateWebSocketStream>());
            return services;
        }

        if (string.Equals(provider, ExchangeProviders.BinanceFuturesTestnet, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<BinanceFuturesExchangeClient>());
            services.AddSingleton<IExchangeConnectionManager>(sp => sp.GetRequiredService<BinanceFuturesExchangeClient>());
            services.AddSingleton<IExchangeEventStream>(sp => sp.GetRequiredService<BinanceUserDataStream>());
            return services;
        }

        if (string.Equals(provider, ExchangeProviders.HyperliquidTestnet, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<HyperliquidExchangeClient>());
            services.AddSingleton<IExchangeConnectionManager>(sp => sp.GetRequiredService<HyperliquidExchangeClient>());
            services.AddSingleton<IExchangeEventStream>(sp => sp.GetRequiredService<HyperliquidUserDataStream>());
            return services;
        }

        if (string.Equals(provider, ExchangeProviders.MexcFuturesReadOnly, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<MexcFuturesReadOnlyExchangeClient>());
            services.AddSingleton<IExchangeConnectionManager>(sp => sp.GetRequiredService<MexcFuturesReadOnlyExchangeClient>());
            services.AddSingleton<IExchangeEventStream>(sp => sp.GetRequiredService<NullExchangeEventStream>());
            return services;
        }

        if (string.Equals(provider, ExchangeProviders.DeribitTestnet, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<DeribitTestnetExchangeClient>());
            services.AddSingleton<IExchangeConnectionManager>(sp => sp.GetRequiredService<DeribitTestnetExchangeClient>());
            services.AddSingleton<IExchangeEventStream>(sp => sp.GetRequiredService<DeribitPrivateWebSocketStream>());
            return services;
        }

        if (string.Equals(provider, ExchangeProviders.OkxDemo, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<OkxDemoExchangeClient>());
            services.AddSingleton<IExchangeConnectionManager>(sp => sp.GetRequiredService<OkxDemoExchangeClient>());
            services.AddSingleton<IExchangeEventStream>(sp => sp.GetRequiredService<OkxPrivateWebSocketStream>());
            return services;
        }

        if (string.Equals(provider, ExchangeProviders.BitgetDemo, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<BitgetDemoExchangeClient>());
            services.AddSingleton<IExchangeConnectionManager>(sp => sp.GetRequiredService<BitgetDemoExchangeClient>());
            services.AddSingleton<IExchangeEventStream>(sp => sp.GetRequiredService<NullExchangeEventStream>());
            return services;
        }

        if (string.Equals(provider, ExchangeProviders.GateFuturesTestnet, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<GateFuturesTestnetExchangeClient>());
            services.AddSingleton<IExchangeConnectionManager>(sp => sp.GetRequiredService<GateFuturesTestnetExchangeClient>());
            services.AddSingleton<IExchangeEventStream>(sp => sp.GetRequiredService<NullExchangeEventStream>());
            return services;
        }

        if (string.Equals(provider, ExchangeProviders.KrakenFuturesReadOnly, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<KrakenFuturesReadOnlyExchangeClient>());
            services.AddSingleton<IExchangeConnectionManager>(sp => sp.GetRequiredService<KrakenFuturesReadOnlyExchangeClient>());
            services.AddSingleton<IExchangeEventStream>(sp => sp.GetRequiredService<NullExchangeEventStream>());
            return services;
        }

        if (string.Equals(provider, ExchangeProviders.KuCoinFuturesReadOnly, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<KuCoinFuturesReadOnlyExchangeClient>());
            services.AddSingleton<IExchangeConnectionManager>(sp => sp.GetRequiredService<KuCoinFuturesReadOnlyExchangeClient>());
            services.AddSingleton<IExchangeEventStream>(sp => sp.GetRequiredService<NullExchangeEventStream>());
            return services;
        }

        if (string.Equals(provider, ExchangeProviders.CoinbaseIntxSandbox, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<CoinbaseIntxSandboxExchangeClient>());
            services.AddSingleton<IExchangeConnectionManager>(sp => sp.GetRequiredService<CoinbaseIntxSandboxExchangeClient>());
            services.AddSingleton<IExchangeEventStream>(sp => sp.GetRequiredService<NullExchangeEventStream>());
            return services;
        }

        if (string.Equals(provider, ExchangeProviders.IbkrPaper, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IExchangeClient>(sp => sp.GetRequiredService<IbkrPaperExchangeClient>());
            services.AddSingleton<IExchangeConnectionManager>(sp => sp.GetRequiredService<IbkrPaperExchangeClient>());
            services.AddSingleton<IExchangeEventStream>(sp => sp.GetRequiredService<NullExchangeEventStream>());
            return services;
        }

        throw new InvalidOperationException(
            $"Unsupported Exchange:Provider '{provider}'. Supported values: {ExchangeProviders.Mock}, {ExchangeProviders.BybitTestnet}, {ExchangeProviders.BinanceFuturesTestnet}, {ExchangeProviders.HyperliquidTestnet}, {ExchangeProviders.MexcFuturesReadOnly}, {ExchangeProviders.DeribitTestnet}, {ExchangeProviders.OkxDemo}, {ExchangeProviders.BitgetDemo}, {ExchangeProviders.GateFuturesTestnet}, {ExchangeProviders.KrakenFuturesReadOnly}, {ExchangeProviders.KuCoinFuturesReadOnly}, {ExchangeProviders.CoinbaseIntxSandbox}, {ExchangeProviders.IbkrPaper}.");
    }

    private static int ReadPositiveInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private static bool ReadBool(string? value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static decimal ReadPositiveDecimal(string? value, decimal fallback) =>
        decimal.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
        && parsed > 0m
            ? parsed
            : fallback;
}
