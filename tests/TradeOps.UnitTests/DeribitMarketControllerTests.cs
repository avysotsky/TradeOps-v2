using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using TradeOps.Api.Controllers;
using Xunit;

namespace TradeOps.UnitTests;

/// <summary>
/// Exercises the actual Deribit market HTTP client with deterministic,
/// entirely in-memory fake JSON-RPC responses. No network or private key.
/// </summary>
public sealed class DeribitMarketControllerTests
{
    private sealed class CaptureHandler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        public readonly List<Uri> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Authorization);
            var uri = Assert.IsType<Uri>(request.RequestUri);
            Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
            Assert.Equal("www.deribit.com", uri.Host);
            Requests.Add(uri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response(request), Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("deribit-terminal", name);
            return client;
        }
    }

    private static DeribitMarketController Make(
        HttpMessageHandler handler, string token = "test-local-operator-session-key-123456")
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["DeribitTerminal:Environment"] = "mainnet",
                ["DeribitTerminal:OperatorKey"] = token,
                ["DeribitTerminal:ExecutionEnabled"] = "false",
                ["DeribitTerminal:MainnetExecutionEnabled"] = "false"
            }).Build();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Request.Headers["X-TradeOps-Terminal-Key"] = token;
        return new DeribitMarketController(new Factory(new HttpClient(handler)), cfg)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    [Fact]
    public async Task RefusesInvalidAndUnauthenticatedRequestsWithoutNetworkCalls()
    {
        var handler = new CaptureHandler(_ => throw new Exception("Unexpected HTTP"));
        var ctrl = Make(handler);
        Assert.IsType<BadRequestObjectResult>(
            await ctrl.Book("https://attacker.invalid/", 10));
        Assert.IsType<BadRequestObjectResult>(
            await ctrl.Trades("ETH-PERPETUAL", 100));
        Assert.IsType<BadRequestObjectResult>(
            await ctrl.Instruments("BTC", "invalid"));
        Assert.IsType<BadRequestObjectResult>(
            await ctrl.Ticker("NOT-IN-SCOPE", default));
        Assert.Empty(handler.Requests);

        ctrl.ControllerContext.HttpContext.Connection.RemoteIpAddress =
            IPAddress.Parse("192.168.5.20");
        Assert.IsType<UnauthorizedObjectResult>(
            await ctrl.Book("ETH-PERPETUAL", 10));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RealSnapshotFormat_BidsAsksReadInNativeAmount()
    {
        var handler = new CaptureHandler(r =>
        {
            Assert.Contains("/api/v2/public/get_order_book", r.RequestUri!.AbsolutePath);
            return """{"jsonrpc":"2.0","result":{"change_id":770,"timestamp":1770000000000,"bids":[[2300.5,15],[2300,22]],"asks":[[2301,9],[2302.5,18]],"mark_price":2300.8}}""";
        });
        var ctrl = Make(handler);
        var result = Assert.IsType<OkObjectResult>(
            await ctrl.Book("ETH-25DEC26", 10));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(2, json.RootElement.GetProperty("bids").GetArrayLength());
        Assert.Equal(15m, json.RootElement.GetProperty("bids")[0].GetProperty("amount").GetDecimal());
        Assert.Equal(2301m, json.RootElement.GetProperty("asks")[0].GetProperty("price").GetDecimal());
        Assert.Single(handler.Requests);
        Assert.Contains("ETH-25DEC26", handler.Requests[0].Query);
    }

    [Fact]
    public async Task LastTradesParsesRealTimePublicTapeOnly()
    {
        var handler = new CaptureHandler(_ =>
            """{"jsonrpc":"2.0","result":{"trades":[{"trade_id":"a1","direction":"buy","price":2324.5,"amount":3,"timestamp":1770000000000}]}}""");
        var ctrl = Make(handler);
        var result = Assert.IsType<OkObjectResult>(
            await ctrl.Trades("ETH-PERPETUAL", 15));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("buy", json.RootElement.GetProperty("trades")[0]
            .GetProperty("direction").GetString());
        Assert.Equal(2324.5m, json.RootElement.GetProperty("trades")[0]
            .GetProperty("price").GetDecimal());
        Assert.Single(handler.Requests);
        Assert.Contains("public/get_last_trades_by_instrument",
            handler.Requests[0].AbsolutePath);
    }

    [Fact]
    public async Task InstrumentsExcludeInactiveAndAllowOptions()
    {
        var handler = new CaptureHandler(_ =>
            """{"jsonrpc":"2.0","result":[{"instrument_name":"ETH-25DEC26-3000-C","kind":"option","is_active":true,"tick_size":0.0005,"expiration_timestamp":1798185600000,"strike":3000,"option_type":"call"},{"instrument_name":"ETH-25DEC26-2500-P","kind":"option","is_active":false},{"instrument_name":"BTC-PERPETUAL","kind":"future","is_active":true}]}""");
        var ctrl = Make(handler);
        var result = Assert.IsType<OkObjectResult>(
            await ctrl.Instruments("ETH", "option", "3000", 20));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(1, json.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal("ETH-25DEC26-3000-C",
            json.RootElement.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Single(handler.Requests);
        Assert.Contains("kind=option", handler.Requests[0].Query);
        Assert.Contains("expired=false", handler.Requests[0].Query);
    }

    [Fact]
    public void ReactUsesLiveMarketReadOnlyRoutesAndKeepsSeparateTradingTicket()
    {
        var root = LocateRoot();
        var code = File.ReadAllText(Path.Combine(root, "apps/trade-terminal/src/Terminal.tsx"));
        var market = File.ReadAllText(Path.Combine(root,
            "apps/TradeOps.TerminalApi/Controllers/DeribitMarketController.cs"));
        Assert.Contains("/market/instruments?", code);
        Assert.Contains("/market/orderbook?instrument=", code);
        Assert.Contains("/market/trades?instrument=", code);
        Assert.Contains("marketInstrument", code);
        Assert.Contains("instrumentSearch", code);
        Assert.Contains("value={instrument}", code);
        Assert.Contains("Инструмент заявки (отдельно от стакана)", code);
        Assert.DoesNotContain("private/buy", market);
        Assert.DoesNotContain("private/sell", market);
        Assert.DoesNotContain("ClientSecret", market);
        Assert.Contains("DateTimeOffset.UtcNow.AddSeconds(60)", market);
        Assert.Contains("Task.Delay(wait, ct)", market);
        Assert.Contains("IPAddress.IsLoopback", market);
        Assert.DoesNotContain("localStorage", code);
        Assert.DoesNotContain("sessionStorage", code);
    }

    private static string LocateRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TradeOps.sln"))) return d.FullName;
        throw new InvalidOperationException("TradeOps root not found.");
    }
}
