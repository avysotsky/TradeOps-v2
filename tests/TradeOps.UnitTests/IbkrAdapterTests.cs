using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange;
using TradeOps.Infrastructure.Exchange.Ibkr;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class IbkrAdapterTests
{
    [Fact]
    public void Constructor_RejectsNonLocalClientPortalGateway()
    {
        var options = new IbkrOptions
        {
            BaseUrl = "https://api.ibkr.com/v1/api"
        };

        var factory = new StaticHttpClientFactory(
            new StubHandler(_ =>
                Json(HttpStatusCode.OK, "{}")));

        var exception = Assert.Throws<InvalidOperationException>(
            () => new IbkrPaperExchangeClient(
                factory,
                options,
                NullLogger<IbkrPaperExchangeClient>.Instance));

        Assert.Contains(
            "local Client Portal Gateway",
            exception.Message);
    }

    [Fact]
    public async Task GetAccountAsync_RejectsNonPaperBrokerageSession()
    {
        var factory = new StaticHttpClientFactory(
            new StubHandler(request =>
            {
                Assert.Equal(
                    "/v1/api/iserver/accounts",
                    request.RequestUri!.AbsolutePath);

                return Json(
                    HttpStatusCode.OK,
                    """
                    {
                      "accounts": ["U1234567"],
                      "selectedAccount": "U1234567",
                      "isPaper": false
                    }
                    """);
            }));

        var client = CreateClient(factory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetAccountAsync());

        Assert.Contains(
            "refuses non-Paper",
            exception.Message);
    }

    [Fact]
    public async Task Resolver_UsesKnownConidAndValidatesIdentity()
    {
        var factory = new StaticHttpClientFactory(
            new StubHandler(request =>
            {
                Assert.Equal(
                    "/v1/api/iserver/contract/265598/info",
                    request.RequestUri!.AbsolutePath);

                return Json(
                    HttpStatusCode.OK,
                    """
                    {
                      "conid": 265598,
                      "ticker": "AAPL",
                      "secType": "STK",
                      "listingExchange": "NASDAQ.NMS",
                      "exchange": "SMART",
                      "currency": "USD",
                      "validExchanges": "SMART,NASDAQ"
                    }
                    """);
            }));

        var resolver = new IbkrInstrumentResolver(
            factory,
            DefaultOptions());

        var resolved = await resolver.ResolveAsync(
            new InstrumentReference(
                "AAPL",
                AssetClass.Stock,
                "USD",
                "265598",
                "SMART"));

        Assert.Equal(265598, resolved.Conid);
        Assert.Equal("AAPL", resolved.Symbol);
        Assert.Equal("USD", resolved.Currency);
        Assert.Equal("SMART", resolved.Exchange);
    }

    [Fact]
    public async Task Resolver_FindsUniqueStockConidFromSymbol()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/iserver/secdef/search",
                    StringComparison.Ordinal))
            {
                Assert.Contains(
                    "symbol=IBM",
                    request.RequestUri.Query);
                Assert.Contains(
                    "secType=STK",
                    request.RequestUri.Query);

                return Json(
                    HttpStatusCode.OK,
                    """
                    [
                      {
                        "conid": "8314",
                        "symbol": "IBM",
                        "description": "NYSE"
                      }
                    ]
                    """);
            }

            Assert.Equal(
                "/v1/api/iserver/contract/8314/info",
                request.RequestUri.AbsolutePath);

            return Json(
                HttpStatusCode.OK,
                """
                {
                  "con_id": 8314,
                  "symbol": "IBM",
                  "instrument_type": "STK",
                  "listing_exchange": "NYSE",
                  "currency": "USD",
                  "valid_exchanges": "SMART,NYSE"
                }
                """);
        });

        var resolver = new IbkrInstrumentResolver(
            new StaticHttpClientFactory(handler),
            DefaultOptions());

        var resolved = await resolver.ResolveAsync(
            new InstrumentReference(
                "IBM",
                AssetClass.Stock,
                "USD",
                null,
                "SMART"));

        Assert.Equal(8314, resolved.Conid);
        Assert.Equal("IBM", resolved.Symbol);
    }

    [Fact]
    public async Task ReadPath_MapsAccountPositionsAndOpenOrders()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith(
                    "/iserver/accounts",
                    StringComparison.Ordinal))
            {
                return Json(
                    HttpStatusCode.OK,
                    """
                    {
                      "accounts": ["DU1234567"],
                      "selectedAccount": "DU1234567",
                      "isPaper": true
                    }
                    """);
            }

            if (path.EndsWith(
                    "/iserver/account/DU1234567/summary",
                    StringComparison.Ordinal))
            {
                return Json(
                    HttpStatusCode.OK,
                    """
                    {
                      "balance": 10000,
                      "availableFunds": 8000,
                      "netLiquidationValue": 12000
                    }
                    """);
            }

            if (path.EndsWith(
                    "/portfolio/accounts",
                    StringComparison.Ordinal))
            {
                return Json(
                    HttpStatusCode.OK,
                    """
                    [
                      {
                        "accountId": "DU1234567",
                        "currency": "USD"
                      }
                    ]
                    """);
            }

            if (path.EndsWith(
                    "/portfolio2/DU1234567/positions",
                    StringComparison.Ordinal))
            {
                return Json(
                    HttpStatusCode.OK,
                    """
                    [
                      {
                        "acctId": "DU1234567",
                        "assetClass": "STK",
                        "contractDesc": "AAPL",
                        "position": 5,
                        "avgPrice": 180,
                        "mktPrice": 190,
                        "unrealizedPnl": 50
                      }
                    ]
                    """);
            }

            if (path.EndsWith(
                    "/iserver/account/orders",
                    StringComparison.Ordinal))
            {
                return Json(
                    HttpStatusCode.OK,
                    """
                    {
                      "orders": [
                        {
                          "account": "DU1234567",
                          "orderId": 123,
                          "ticker": "AAPL",
                          "secType": "STK",
                          "remainingQuantity": 3,
                          "filledQuantity": 2,
                          "totalSize": 5,
                          "status": "Submitted",
                          "avgPrice": "185",
                          "origOrderType": "LIMIT",
                          "order_ref": "client-1",
                          "side": "BUY",
                          "lastExecutionTime_r": 1700000000000
                        }
                      ],
                      "snapshot": true
                    }
                    """);
            }

            throw new Xunit.Sdk.XunitException(
                $"Unexpected IBKR request: {request.Method} {request.RequestUri}");
        });

        var client = CreateClient(
            new StaticHttpClientFactory(handler));

        var account = await client.GetAccountAsync();
        var positions = await client.GetPositionsAsync();
        var orders = await client.GetOpenOrdersAsync();

        Assert.Equal("USD", account.Currency);
        Assert.Equal(10000m, account.Balance);
        Assert.Equal(12000m, account.Equity);
        Assert.Equal(8000m, account.AvailableBalance);

        var position = Assert.Single(positions);
        Assert.Equal("AAPL", position.Symbol);
        Assert.Equal(OrderSide.Buy, position.Side);
        Assert.Equal(5m, position.Quantity);
        Assert.Equal(180m, position.AverageEntryPrice);
        Assert.Equal(190m, position.MarkPrice);
        Assert.Equal(50m, position.UnrealizedPnL);

        var order = Assert.Single(orders);
        Assert.Equal("123", order.ExchangeOrderId);
        Assert.Equal("client-1", order.ClientOrderId);
        Assert.Equal("AAPL", order.Symbol);
        Assert.Equal(OrderSide.Buy, order.Side);
        Assert.Equal(OrderType.Limit, order.OrderType);
        Assert.Equal(5m, order.RequestedQuantity);
        Assert.Equal(2m, order.FilledQuantity);
        Assert.Equal(185m, order.AverageFillPrice);
        Assert.Equal(OrderStatus.PartiallyFilled, order.Status);
    }

    [Fact]
    public async Task Mutations_AreDisabledInFirstPaperMilestone()
    {
        var client = CreateClient(
            new StaticHttpClientFactory(
                new StubHandler(_ =>
                    Json(HttpStatusCode.OK, "{}"))));

        await Assert.ThrowsAsync<NotSupportedException>(
            () => client.PlaceOrderAsync(
                new PlaceOrderRequest(
                    "client-1",
                    "AAPL",
                    OrderSide.Buy,
                    OrderType.Market,
                    1m)));

        await Assert.ThrowsAsync<NotSupportedException>(
            () => client.CancelOrderAsync("123"));
    }


    [Fact]
    public void CapabilityCatalog_IbkrPaperRemainsReadOnly()
    {
        var catalog = new ExchangeCapabilityCatalog(
            new ExchangeOptions
            {
                Provider = ExchangeProviders.IbkrPaper
            });

        var profile = catalog.Current;

        Assert.Equal("Paper", profile.Environment);
        Assert.True(profile.SupportsAccountRead);
        Assert.True(profile.SupportsPositionRead);
        Assert.True(profile.SupportsOpenOrdersRead);
        Assert.True(profile.SupportsOrderLookup);
        Assert.False(profile.SupportsOrderPlacement);
        Assert.False(profile.SupportsOrderCancellation);
        Assert.False(profile.SupportsPrivateEventStream);
    }

    private static IbkrPaperExchangeClient CreateClient(
        IHttpClientFactory factory)
    {
        var options = DefaultOptions();

        return new IbkrPaperExchangeClient(
            factory,
            options,
            NullLogger<IbkrPaperExchangeClient>.Instance);
    }

    private static IbkrOptions DefaultOptions() =>
        new()
        {
            BaseUrl = "https://localhost:5000/v1/api",
            AccountId = "DU1234567",
            AccountCurrency = "USD"
        };

    private static HttpResponseMessage Json(
        HttpStatusCode statusCode,
        string json) =>
        new(statusCode)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json")
        };

    private sealed class StaticHttpClientFactory(
        HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client =
            new(handler, disposeHandler: false);

        public HttpClient CreateClient(string name)
        {
            _ = name;
            return _client;
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(respond(request));
        }
    }
}
