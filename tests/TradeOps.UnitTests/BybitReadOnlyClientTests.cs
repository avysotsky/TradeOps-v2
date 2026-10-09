using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Bybit;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class BybitReadOnlyClientTests
{
    [Fact]
    public async Task ReadOnlyFlow_SignsRequests_AndMapsAccountPositionsAndOrders()
    {
        var options = new BybitOptions
        {
            BaseUrl = "https://api-testnet.bybit.com",
            ApiKey = "test-key",
            ApiSecret = "test-secret",
            AccountType = "UNIFIED",
            Category = "linear",
            SettleCoin = "USDT",
            RecvWindowMilliseconds = 5_000
        };

        var requestedPaths = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requestedPaths.Add(request.RequestUri!.AbsolutePath);
            AssertAuthenticatedRequest(request, options);

            return request.RequestUri.AbsolutePath switch
            {
                "/v5/account/wallet-balance" => JsonResponse("""
                    {
                      "retCode": 0,
                      "retMsg": "OK",
                      "result": {
                        "list": [{
                          "totalEquity": "1001.25",
                          "totalWalletBalance": "1000.00",
                          "totalAvailableBalance": "850.50"
                        }]
                      }
                    }
                    """),
                "/v5/position/list" => JsonResponse("""
                    {
                      "retCode": 0,
                      "retMsg": "OK",
                      "result": {
                        "list": [{
                          "symbol": "BTCUSDT",
                          "side": "Buy",
                          "size": "0.002",
                          "avgPrice": "63500",
                          "markPrice": "64000",
                          "unrealisedPnl": "1.00"
                        }]
                      }
                    }
                    """),
                "/v5/order/realtime" => JsonResponse("""
                    {
                      "retCode": 0,
                      "retMsg": "OK",
                      "result": {
                        "list": [{
                          "orderId": "exchange-123",
                          "orderLinkId": "trd-d0f8625f2ad444eba1ec22acbfbb2e58",
                          "symbol": "BTCUSDT",
                          "side": "Buy",
                          "orderType": "Limit",
                          "qty": "0.001",
                          "cumExecQty": "0",
                          "avgPrice": "",
                          "price": "62000",
                          "orderStatus": "New",
                          "createdTime": "1760000000000",
                          "updatedTime": "1760000001000"
                        }]
                      }
                    }
                    """),
                _ => throw new InvalidOperationException(
                    $"Unexpected Bybit request path '{request.RequestUri.AbsolutePath}'.")
            };
        });

        var client = CreateClient(options, handler);

        var account = await client.GetAccountAsync();
        var positions = await client.GetPositionsAsync();
        var orders = await client.GetOpenOrdersAsync();

        Assert.Equal("USD", account.Currency);
        Assert.Equal(1000.00m, account.Balance);
        Assert.Equal(1001.25m, account.Equity);
        Assert.Equal(850.50m, account.AvailableBalance);

        var position = Assert.Single(positions);
        Assert.Equal("BTCUSDT", position.Symbol);
        Assert.Equal(OrderSide.Buy, position.Side);
        Assert.Equal(0.002m, position.Quantity);
        Assert.Equal(63_500m, position.AverageEntryPrice);
        Assert.Equal(64_000m, position.MarkPrice);
        Assert.Equal(1.00m, position.UnrealizedPnL);

        var order = Assert.Single(orders);
        Assert.Equal("exchange-123", order.ExchangeOrderId);
        Assert.Equal("trd-d0f8625f2ad444eba1ec22acbfbb2e58", order.ClientOrderId);
        Assert.Equal(OrderStatus.Accepted, order.Status);
        Assert.Equal(OrderType.Limit, order.OrderType);
        Assert.Equal(0.001m, order.RequestedQuantity);
        Assert.Equal(62_000m, order.Price);

        Assert.Equal(
            [
                "/v5/account/wallet-balance",
                "/v5/position/list",
                "/v5/order/realtime"
            ],
            requestedPaths);
    }

    [Fact]
    public async Task EnsureConnectedAsync_UsesAuthenticatedAccountRead_AndMarksConnected()
    {
        var options = new BybitOptions
        {
            BaseUrl = "https://api-testnet.bybit.com",
            ApiKey = "test-key",
            ApiSecret = "test-secret"
        };

        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal("/v5/account/wallet-balance", request.RequestUri!.AbsolutePath);
            AssertAuthenticatedRequest(request, options);

            return JsonResponse("""
                {
                  "retCode": 0,
                  "retMsg": "OK",
                  "result": {
                    "list": [{
                      "totalEquity": "1000",
                      "totalWalletBalance": "1000",
                      "totalAvailableBalance": "1000"
                    }]
                  }
                }
                """);
        });

        var client = CreateClient(options, handler);

        await client.EnsureConnectedAsync();

        Assert.True(client.IsConnected);
    }

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

    private static void AssertAuthenticatedRequest(
        HttpRequestMessage request,
        BybitOptions options)
    {
        Assert.True(request.Headers.TryGetValues("X-BAPI-API-KEY", out var apiKeys));
        Assert.Equal(options.ApiKey, Assert.Single(apiKeys));

        Assert.True(request.Headers.TryGetValues("X-BAPI-TIMESTAMP", out var timestamps));
        var timestampText = Assert.Single(timestamps);
        Assert.True(long.TryParse(timestampText, out var timestamp));

        Assert.True(request.Headers.TryGetValues("X-BAPI-RECV-WINDOW", out var receiveWindows));
        Assert.Equal(options.RecvWindowMilliseconds.ToString(), Assert.Single(receiveWindows));

        Assert.True(request.Headers.TryGetValues("X-BAPI-SIGN", out var signatures));
        var actualSignature = Assert.Single(signatures);
        var query = request.RequestUri!.Query.TrimStart('?');
        var expectedSignature = BybitSigner.CreateHmacSha256Hex(
            options.ApiSecret,
            timestamp,
            options.ApiKey,
            options.RecvWindowMilliseconds,
            query);

        Assert.Equal(expectedSignature, actualSignature);
        Assert.DoesNotContain(options.ApiSecret, request.ToString(), StringComparison.Ordinal);
    }

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
