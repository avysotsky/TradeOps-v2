using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange;
using TradeOps.Infrastructure.Exchange.Ibkr;

namespace TradeOps.IbkrOfflineDemo;

/// <summary>
/// Account-free, deterministic replay through the REAL existing IBKR Web API
/// adapter. No socket connection, IBKR login, credentials, market data,
/// live account or broker mutation is possible with this HTTP handler.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 0)
        {
            Console.Error.WriteLine("IBKR OFFLINE DEMO FAIL: unexpected arguments.");
            return 2;
        }

        try
        {
            var options = new IbkrOptions
            {
                BaseUrl = "https://localhost:5000/v1/api",
                AccountId = "DU0000000", // SYNTHETIC, never a customer ID
                AccountCurrency = "USD"
            };

            var profile = new ExchangeCapabilityCatalog(new ExchangeOptions
            {
                Provider = ExchangeProviders.IbkrPaper
            }).Current;

            Require(!profile.SupportsOrderPlacement &&
                    !profile.SupportsOrderCancellation &&
                    !profile.SupportsPrivateEventStream,
                "Unsafe IBKR capabilities were enabled.");

            var replay = new SyntheticIbkrTransport();
            using var http = new HttpClient(replay);
            var factory = new SingleHttpClientFactory(http);
            var client = new IbkrPaperExchangeClient(
                factory, options, NullLogger<IbkrPaperExchangeClient>.Instance);
            var resolver = new IbkrInstrumentResolver(factory, options);

            await client.EnsureConnectedAsync();
            Require(client.IsConnected, "Synthetic Paper handshake not connected.");

            var account = await client.GetAccountAsync();
            var positions = await client.GetPositionsAsync();
            var orders = await client.GetOpenOrdersAsync();

            Require(account.Currency == "USD" &&
                    account.Balance == 10_000m &&
                    account.Equity == 12_000m &&
                    account.AvailableBalance == 8_000m,
                "Synthetic account mapping differs from contract.");
            Require(positions.Count == 1, "Synthetic stock position not mapped.");
            Require(orders.Count == 1, "Synthetic open order not mapped.");

            var byConid = await resolver.ResolveAsync(new InstrumentReference(
                "AAPL", AssetClass.Stock, "USD", "265598", "SMART"));
            var bySymbol = await resolver.ResolveAsync(new InstrumentReference(
                "AAPL", AssetClass.Stock, "USD", null, "SMART"));
            Require(byConid.Conid == 265598 && bySymbol.Conid == 265598,
                "Synthetic contract resolution failed.");

            try
            {
                await client.PlaceOrderAsync(new PlaceOrderRequest(
                    "offline-only", "AAPL", OrderSide.Buy, OrderType.Market, 1m));
                throw new InvalidOperationException("Mutation was not rejected.");
            }
            catch (NotSupportedException)
            {
                // Expected fail-closed behavior of the REAL IBKR adapter.
            }

            Require(replay.PostCount == 0, "Unexpected IBKR POST occurred.");
            Require(replay.RequestCount >= 7, "Synthetic read path was not exercised.");

            // Independently verify the real adapter REJECTS a non-Paper session.
            using var liveResponseHandler = new SyntheticIbkrTransport(isPaper: false);
            using var liveResponseClient = new HttpClient(liveResponseHandler);
            var liveAdapter = new IbkrPaperExchangeClient(
                new SingleHttpClientFactory(liveResponseClient),
                options, NullLogger<IbkrPaperExchangeClient>.Instance);
            try
            {
                await liveAdapter.EnsureConnectedAsync();
                throw new InvalidOperationException("Non-Paper session was accepted.");
            }
            catch (InvalidOperationException error) when (
                error.Message.Contains("refuses non-Paper", StringComparison.Ordinal))
            {
                // Intentional local read-path guard.
            }

            Require(liveResponseHandler.RequestCount == 1 &&
                    liveResponseHandler.PostCount == 0,
                "Live guard reached more than the expected synthetic handshake.");

            Console.WriteLine(
                "IBKR ACCOUNT-FREE OFFLINE DEMO PASS " +
                "sessions=paper+live-rejected " +
                "account=1 positions=1 open_orders=1 " +
                "contract_resolutions=2 mutations=blocked " +
                "external_requests=0 real_broker_connection=false");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(
                "IBKR ACCOUNT-FREE OFFLINE DEMO FAIL reason=" +
                (error is HttpRequestException ? "SYNTHETIC_HTTP" : "REPLAY_ASSERTION"));
            return 2;
        }
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
        {
            throw new InvalidOperationException(reason);
        }
    }

    private sealed class SingleHttpClientFactory(HttpClient client)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            if (name != IbkrPaperExchangeClient.HttpClientName)
            {
                throw new InvalidOperationException("Unexpected HTTP client.");
            }
            return client;
        }
    }

    private sealed class SyntheticIbkrTransport(bool isPaper = true)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public int PostCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;

            var uri = request.RequestUri ??
                throw new InvalidOperationException("Missing replay URI.");
            if (uri.Scheme != "https" || uri.Host != "localhost" ||
                uri.Port != 5000 || !uri.AbsolutePath.StartsWith(
                    "/v1/api/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Replay request escaped local CPGW.");
            }

            if (request.Method != HttpMethod.Get)
            {
                PostCount++;
                throw new InvalidOperationException("Replay rejects every mutation.");
            }

            var path = uri.PathAndQuery["/v1/api".Length..];
            var json = path switch
            {
                "/iserver/accounts" when isPaper =>
                    """{"accounts":["DU0000000"],"selectedAccount":"DU0000000","isPaper":true}""",
                "/iserver/accounts" =>
                    """{"accounts":["U0000000"],"selectedAccount":"U0000000","isPaper":false}""",
                "/iserver/account/DU0000000/summary" =>
                    """{"balance":10000,"availableFunds":8000,"netLiquidationValue":12000}""",
                "/portfolio/accounts" =>
                    """[{"accountId":"DU0000000","currency":"USD"}]""",
                "/portfolio2/DU0000000/positions" =>
                    """[{"acctId":"DU0000000","assetClass":"STK","contractDesc":"AAPL","position":5,"avgPrice":180,"mktPrice":190,"unrealizedPnl":50}]""",
                "/iserver/account/orders?force=true&accountId=DU0000000" =>
                    """{"orders":[{"account":"DU0000000","orderId":123,"ticker":"AAPL","secType":"STK","remainingQuantity":3,"filledQuantity":2,"totalSize":5,"status":"Submitted","avgPrice":"185","origOrderType":"LIMIT","order_ref":"offline-1","side":"BUY","lastExecutionTime_r":1700000000000}]}""",
                "/iserver/contract/265598/info" =>
                    """{"conid":265598,"ticker":"AAPL","secType":"STK","listingExchange":"NASDAQ.NMS","currency":"USD","validExchanges":"SMART,NASDAQ"}""",
                "/iserver/secdef/search?symbol=AAPL" =>
                    """[{"conid":"265598","symbol":"AAPL","description":"NASDAQ"}]""",
                _ => throw new InvalidOperationException("Unexpected read-path request.")
            };

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
