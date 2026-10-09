using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Bybit;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class BybitExecutionClientTests
{
    private const string ClientOrderId = "trd-d0f8625f2ad444eba1ec22acbfbb2e58";

    [Fact]
    public async Task PlaceOrderAsync_ValidMarketOrder_ValidatesInstrumentSignsCreateAndReturnsAccepted()
    {
        var options = CreateOptions();
        var requestedPaths = new List<string>();

        var handler = new StubHttpMessageHandler(request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);

            return request.RequestUri.AbsolutePath switch
            {
                "/v5/market/instruments-info" => InstrumentResponse(),
                "/v5/order/create" => HandleCreateRequest(request, options),
                _ => throw new InvalidOperationException(
                    $"Unexpected Bybit request path '{request.RequestUri.AbsolutePath}'.")
            };
        });

        var client = CreateClient(options, handler);
        var request = new PlaceOrderRequest(
            ClientOrderId,
            "btcusdt",
            OrderSide.Buy,
            OrderType.Market,
            0.001m);

        var result = await client.PlaceOrderAsync(request);

        Assert.Equal("exchange-123", result.ExchangeOrderId);
        Assert.Equal(ClientOrderId, result.ClientOrderId);
        Assert.Equal(OrderStatus.Accepted, result.Status);
        Assert.Equal(0m, result.FilledQuantity);
        Assert.Equal(
            ["/v5/market/instruments-info", "/v5/order/create"],
            requestedPaths);
    }

    [Fact]
    public async Task PlaceOrderAsync_DuplicateRequest_LoadsExistingOrderByClientOrderId()
    {
        var options = CreateOptions();
        var requestedPaths = new List<string>();

        var handler = new StubHttpMessageHandler(request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);

            if (request.RequestUri.AbsolutePath == "/v5/market/instruments-info")
            {
                return InstrumentResponse();
            }

            if (request.RequestUri.AbsolutePath == "/v5/order/create")
            {
                AssertPrivatePostSignature(request, options);
                return JsonResponse("""
                    {
                      "retCode": 10014,
                      "retMsg": "Invalid duplicate request",
                      "result": {}
                    }
                    """);
            }

            if (request.RequestUri.AbsolutePath == "/v5/order/realtime")
            {
                AssertAuthenticatedGet(request, options);
                Assert.Contains($"orderLinkId={ClientOrderId}", request.RequestUri.Query);
                return OrderListResponse("Filled", "0.001", "64100");
            }

            throw new InvalidOperationException(
                $"Unexpected Bybit request path '{request.RequestUri.AbsolutePath}'.");
        });

        var client = CreateClient(options, handler);

        var result = await client.PlaceOrderAsync(new PlaceOrderRequest(
            ClientOrderId,
            "BTCUSDT",
            OrderSide.Buy,
            OrderType.Market,
            0.001m));

        Assert.Equal("exchange-123", result.ExchangeOrderId);
        Assert.Equal(ClientOrderId, result.ClientOrderId);
        Assert.Equal(OrderStatus.Filled, result.Status);
        Assert.Equal(0.001m, result.FilledQuantity);
        Assert.Equal(64_100m, result.AverageFillPrice);
        Assert.Equal(
            [
                "/v5/market/instruments-info",
                "/v5/order/create",
                "/v5/order/realtime"
            ],
            requestedPaths);
    }

    [Fact]
    public async Task GetOrderAsync_RealtimeMiss_FallsBackToHistory()
    {
        var options = CreateOptions();
        var requestedPaths = new List<string>();

        var handler = new StubHttpMessageHandler(request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);
            AssertAuthenticatedGet(request, options);
            Assert.Contains("orderId=exchange-123", request.RequestUri.Query);

            return request.RequestUri.AbsolutePath switch
            {
                "/v5/order/realtime" => EmptyOrderListResponse(),
                "/v5/order/history" => OrderListResponse("Filled", "0.001", "64100"),
                _ => throw new InvalidOperationException(
                    $"Unexpected Bybit request path '{request.RequestUri.AbsolutePath}'.")
            };
        });

        var client = CreateClient(options, handler);

        var order = await client.GetOrderAsync("exchange-123");

        Assert.NotNull(order);
        Assert.Equal(OrderStatus.Filled, order.Status);
        Assert.Equal(0.001m, order.FilledQuantity);
        Assert.Equal(
            ["/v5/order/realtime", "/v5/order/history"],
            requestedPaths);
    }

    [Fact]
    public async Task CancelOrderAsync_ExistingOpenOrder_SendsSignedCancelRequest()
    {
        var options = CreateOptions();
        var requestedPaths = new List<string>();

        var handler = new StubHttpMessageHandler(request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);

            if (request.RequestUri.AbsolutePath == "/v5/order/realtime")
            {
                AssertAuthenticatedGet(request, options);
                Assert.Contains("orderId=exchange-123", request.RequestUri.Query);
                return OrderListResponse("New", "0", "");
            }

            if (request.RequestUri.AbsolutePath == "/v5/order/cancel")
            {
                AssertPrivatePostSignature(request, options);
                var body = ReadJsonBody(request);
                Assert.Equal("linear", body.RootElement.GetProperty("category").GetString());
                Assert.Equal("BTCUSDT", body.RootElement.GetProperty("symbol").GetString());
                Assert.Equal("exchange-123", body.RootElement.GetProperty("orderId").GetString());

                return JsonResponse($$"""
                    {
                      "retCode": 0,
                      "retMsg": "OK",
                      "result": {
                        "orderId": "exchange-123",
                        "orderLinkId": "{{ClientOrderId}}"
                      }
                    }
                    """);
            }

            throw new InvalidOperationException(
                $"Unexpected Bybit request path '{request.RequestUri.AbsolutePath}'.");
        });

        var client = CreateClient(options, handler);

        await client.CancelOrderAsync("exchange-123");

        Assert.Equal(
            ["/v5/order/realtime", "/v5/order/cancel"],
            requestedPaths);
    }

    [Fact]
    public async Task PlaceOrderAsync_ExplicitBybitReject_ReturnsRejectedWithoutLookupRetry()
    {
        var options = CreateOptions();
        var requestedPaths = new List<string>();

        var handler = new StubHttpMessageHandler(request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);

            if (request.RequestUri.AbsolutePath == "/v5/market/instruments-info")
            {
                return InstrumentResponse();
            }

            if (request.RequestUri.AbsolutePath == "/v5/order/create")
            {
                AssertPrivatePostSignature(request, options);
                return JsonResponse("""
                    {
                      "retCode": 110017,
                      "retMsg": "Reduce-only rule not satisfied",
                      "result": {}
                    }
                    """);
            }

            throw new InvalidOperationException(
                $"Unexpected Bybit request path '{request.RequestUri.AbsolutePath}'.");
        });

        var client = CreateClient(options, handler);

        var result = await client.PlaceOrderAsync(new PlaceOrderRequest(
            ClientOrderId,
            "BTCUSDT",
            OrderSide.Buy,
            OrderType.Market,
            0.001m));

        Assert.Null(result.ExchangeOrderId);
        Assert.Equal(OrderStatus.Rejected, result.Status);
        Assert.Equal(
            ["/v5/market/instruments-info", "/v5/order/create"],
            requestedPaths);
    }

    private static HttpResponseMessage HandleCreateRequest(
        HttpRequestMessage request,
        BybitOptions options)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        AssertPrivatePostSignature(request, options);

        var body = ReadJsonBody(request);
        Assert.Equal("linear", body.RootElement.GetProperty("category").GetString());
        Assert.Equal("BTCUSDT", body.RootElement.GetProperty("symbol").GetString());
        Assert.Equal("Buy", body.RootElement.GetProperty("side").GetString());
        Assert.Equal("Market", body.RootElement.GetProperty("orderType").GetString());
        Assert.Equal("0.001", body.RootElement.GetProperty("qty").GetString());
        Assert.Equal(ClientOrderId, body.RootElement.GetProperty("orderLinkId").GetString());

        return JsonResponse($$"""
            {
              "retCode": 0,
              "retMsg": "OK",
              "result": {
                "orderId": "exchange-123",
                "orderLinkId": "{{ClientOrderId}}"
              }
            }
            """);
    }

    private static BybitOptions CreateOptions() => new()
    {
        BaseUrl = "https://api-testnet.bybit.com",
        ApiKey = "test-key",
        ApiSecret = "test-secret",
        AccountType = "UNIFIED",
        Category = "linear",
        SettleCoin = "USDT",
        RecvWindowMilliseconds = 5_000
    };

    private static BybitExchangeClient CreateClient(
        BybitOptions options,
        HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        return new BybitExchangeClient(
            new StubHttpClientFactory(httpClient),
            options,
            NullLogger<BybitExchangeClient>.Instance);
    }

    private static void AssertAuthenticatedGet(
        HttpRequestMessage request,
        BybitOptions options)
    {
        AssertAuthenticationHeaders(request, options, request.RequestUri!.Query.TrimStart('?'));
    }

    private static void AssertPrivatePostSignature(
        HttpRequestMessage request,
        BybitOptions options)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        AssertAuthenticationHeaders(request, options, body);
    }

    private static void AssertAuthenticationHeaders(
        HttpRequestMessage request,
        BybitOptions options,
        string signedPayload)
    {
        Assert.True(request.Headers.TryGetValues("X-BAPI-API-KEY", out var apiKeys));
        Assert.Equal(options.ApiKey, Assert.Single(apiKeys));

        Assert.True(request.Headers.TryGetValues("X-BAPI-TIMESTAMP", out var timestamps));
        var timestampText = Assert.Single(timestamps);
        Assert.True(long.TryParse(timestampText, out var timestamp));

        Assert.True(request.Headers.TryGetValues("X-BAPI-RECV-WINDOW", out var receiveWindows));
        Assert.Equal(options.RecvWindowMilliseconds.ToString(), Assert.Single(receiveWindows));

        Assert.True(request.Headers.TryGetValues("X-BAPI-SIGN", out var signatures));
        var expectedSignature = BybitSigner.CreateHmacSha256Hex(
            options.ApiSecret,
            timestamp,
            options.ApiKey,
            options.RecvWindowMilliseconds,
            signedPayload);

        Assert.Equal(expectedSignature, Assert.Single(signatures));
    }

    private static JsonDocument ReadJsonBody(HttpRequestMessage request)
    {
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return JsonDocument.Parse(body);
    }

    private static HttpResponseMessage InstrumentResponse() => JsonResponse("""
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
        """);

    private static HttpResponseMessage EmptyOrderListResponse() => JsonResponse("""
        {
          "retCode": 0,
          "retMsg": "OK",
          "result": {
            "list": []
          }
        }
        """);

    private static HttpResponseMessage OrderListResponse(
        string status,
        string cumulativeExecutedQuantity,
        string averagePrice) => JsonResponse($$"""
        {
          "retCode": 0,
          "retMsg": "OK",
          "result": {
            "list": [{
              "orderId": "exchange-123",
              "orderLinkId": "{{ClientOrderId}}",
              "symbol": "BTCUSDT",
              "side": "Buy",
              "orderType": "Market",
              "qty": "0.001",
              "cumExecQty": "{{cumulativeExecutedQuantity}}",
              "avgPrice": "{{averagePrice}}",
              "price": "",
              "orderStatus": "{{status}}",
              "createdTime": "1760000000000",
              "updatedTime": "1760000001000"
            }]
          }
        }
        """);

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
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
