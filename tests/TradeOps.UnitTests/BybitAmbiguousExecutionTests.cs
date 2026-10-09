using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Bybit;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class BybitAmbiguousExecutionTests
{
    [Fact]
    public async Task PlaceOrderAsync_NetworkFailureAfterCreateRequest_ThrowsTimeoutExceptionWithoutRetry()
    {
        var options = new BybitOptions
        {
            BaseUrl = "https://api-testnet.bybit.com",
            ApiKey = "test-key",
            ApiSecret = "test-secret",
            Category = "linear",
            SettleCoin = "USDT",
            RecvWindowMilliseconds = 5_000
        };
        var requestedPaths = new List<string>();
        var createCalls = 0;

        var handler = new StubHttpMessageHandler(request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);

            if (request.RequestUri.AbsolutePath == "/v5/market/instruments-info")
            {
                return InstrumentResponse();
            }

            if (request.RequestUri.AbsolutePath == "/v5/order/create")
            {
                createCalls++;
                throw new HttpRequestException("connection dropped after request was sent");
            }

            throw new InvalidOperationException(
                $"Unexpected Bybit request path '{request.RequestUri.AbsolutePath}'.");
        });

        var client = new BybitExchangeClient(
            new StubHttpClientFactory(new HttpClient(handler)),
            options,
            NullLogger<BybitExchangeClient>.Instance);

        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            client.PlaceOrderAsync(new PlaceOrderRequest(
                "trd-d0f8625f2ad444eba1ec22acbfbb2e58",
                "BTCUSDT",
                OrderSide.Buy,
                OrderType.Market,
                0.001m)));

        Assert.Contains("ambiguous", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, createCalls);
        Assert.Equal(
            ["/v5/market/instruments-info", "/v5/order/create"],
            requestedPaths);
    }

    private static HttpResponseMessage InstrumentResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {
              "retCode": 0,
              "retMsg": "OK",
              "result": {
                "list": [{
                  "symbol": "BTCUSDT",
                  "status": "Trading",
                  "priceFilter": {
                    "minPrice": "1",
                    "maxPrice": "1000000",
                    "tickSize": "0.1"
                  },
                  "lotSizeFilter": {
                    "minNotionalValue": "5",
                    "maxOrderQty": "100",
                    "maxMktOrderQty": "100",
                    "minOrderQty": "0.001",
                    "qtyStep": "0.001"
                  }
                }]
              }
            }
            """, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
