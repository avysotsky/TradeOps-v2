using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange;
using TradeOps.Infrastructure.Exchange.Ibkr;

namespace TradeOps.IbkrPaperSmoke;

internal static class Program
{
    private const string BaseUrl = "https://localhost:5000/v1/api";
    private const string OptInValue = "RUN_PAPER_READ_ONLY_SMOKE";

    public static async Task<int> Main()
    {
        try
        {
            RequireExplicitOptIn();
            AssertReadOnlyCapability();

            var accountId = ReadOptional("TRADEOPS_IBKR_PAPER_ACCOUNT_ID");
            var accountCurrency =
                ReadOptional("TRADEOPS_IBKR_PAPER_ACCOUNT_CURRENCY")
                ?? "USD";

            var symbol =
                (ReadOptional("TRADEOPS_IBKR_PAPER_SMOKE_SYMBOL")
                 ?? "AAPL")
                .ToUpperInvariant();

            var currency =
                (ReadOptional("TRADEOPS_IBKR_PAPER_SMOKE_CURRENCY")
                 ?? "USD")
                .ToUpperInvariant();

            var knownConid =
                ReadOptional("TRADEOPS_IBKR_PAPER_SMOKE_CONID")
                ?? "265598";

            var exchange =
                ReadOptional("TRADEOPS_IBKR_PAPER_SMOKE_EXCHANGE");

            var orderId =
                ReadOptional("TRADEOPS_IBKR_PAPER_SMOKE_ORDER_ID");

            var options = new IbkrOptions
            {
                BaseUrl = BaseUrl,
                AccountId = accountId ?? string.Empty,
                AccountCurrency = accountCurrency,
                HttpTimeoutSeconds = 15,
                AllowUntrustedLocalhostCertificate = true
            };

            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

            using var httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds)
            };

            var factory = new SingleHttpClientFactory(httpClient);

            var client = new IbkrPaperExchangeClient(
                factory,
                options,
                NullLogger<IbkrPaperExchangeClient>.Instance);

            var resolver = new IbkrInstrumentResolver(
                factory,
                options);

            Console.WriteLine("Provider: IbkrPaper");

            await client.EnsureConnectedAsync();
            Console.WriteLine("Paper session confirmed: PASS");
            Console.WriteLine("Account selected: PASS");

            _ = await client.GetAccountAsync();
            Console.WriteLine("Account summary retrieved: PASS");

            var positions = await client.GetPositionsAsync();
            Console.WriteLine($"Positions count: {positions.Count}");

            var known = await resolver.ResolveAsync(
                new InstrumentReference(
                    symbol,
                    AssetClass.Stock,
                    currency,
                    knownConid,
                    exchange));

            Console.WriteLine(
                $"Instrument resolution (known conid): PASS {known.Symbol}/{known.Currency} conid={known.Conid}");

            var discovered = await resolver.ResolveAsync(
                new InstrumentReference(
                    symbol,
                    AssetClass.Stock,
                    currency,
                    VenueInstrumentId: null,
                    Exchange: exchange));

            Console.WriteLine(
                $"Instrument resolution (symbol+currency): PASS {discovered.Symbol}/{discovered.Currency} conid={discovered.Conid}");

            var openOrders = await client.GetOpenOrdersAsync();
            Console.WriteLine($"Open orders count: {openOrders.Count}");

            if (string.IsNullOrWhiteSpace(orderId))
            {
                Console.WriteLine(
                    "Order lookup: SKIPPED (no runtime order id supplied)");
            }
            else
            {
                var order = await client.GetOrderAsync(orderId);

                if (order is null)
                {
                    throw new InvalidOperationException(
                        "Supplied Paper order was not found.");
                }

                Console.WriteLine(
                    $"Order lookup: PASS status={order.Status}");
            }

            Console.WriteLine("REAL IBKR PAPER READ-PATH SMOKE: PASS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"REAL IBKR PAPER READ-PATH SMOKE: FAIL ({exception.GetType().Name}: {exception.Message})");
            return 1;
        }
    }

    private static void AssertReadOnlyCapability()
    {
        var catalog = new ExchangeCapabilityCatalog(
            new ExchangeOptions
            {
                Provider = ExchangeProviders.IbkrPaper
            });

        var profile = catalog.Current;

        if (profile.SupportsOrderPlacement
            || profile.SupportsOrderCancellation)
        {
            throw new InvalidOperationException(
                "IBKR Paper smoke refuses to run because mutation capability is enabled.");
        }
    }

    private static void RequireExplicitOptIn()
    {
        var value = Environment.GetEnvironmentVariable(
            "TRADEOPS_IBKR_PAPER_SMOKE_CONFIRM");

        if (!string.Equals(
                value,
                OptInValue,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Explicit opt-in required: TRADEOPS_IBKR_PAPER_SMOKE_CONFIRM={OptInValue}.");
        }
    }

    private static string? ReadOptional(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);

        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private sealed class SingleHttpClientFactory(
        HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            _ = name;
            return httpClient;
        }
    }
}
