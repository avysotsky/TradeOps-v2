using System.Net;
using System.Net.Http.Headers;
using System.Text;
using TradeOps.Infrastructure.Exchange.Deribit;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitLiveReadOnlyClientTests
{
    private const string Basic = "Basic";
    private static readonly string AuthorizationValue = Convert.ToBase64String(
        Encoding.UTF8.GetBytes("read-only-id:read-only-secret"));

    [Fact]
    public async Task Probe_OnlyThreeWhitelistedMainnetGets_NoAccountDataOutput()
    {
        var paths = new List<string>();
        using var http = NewClient(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("www.deribit.com", request.RequestUri.Host);
            Assert.Equal(Basic, request.Headers.Authorization!.Scheme);
            Assert.Equal(AuthorizationValue,
                request.Headers.Authorization.Parameter);
            Assert.Null(request.Content);
            paths.Add(request.RequestUri.PathAndQuery);
            return request.RequestUri.PathAndQuery switch
            {
                "/api/v2/private/get_account_summaries" =>
                    Ok("""{"result":{"summaries":[{"currency":"BTC"},{"currency":"ETH"}]},"jsonrpc":"2.0"}"""),
                "/api/v2/private/get_positions?currency=any" =>
                    Ok("""{"result":[{"instrument_name":"BTC-PERPETUAL","kind":"future","direction":"buy","size":100,"size_currency":0.002}],"jsonrpc":"2.0"}"""),
                "/api/v2/private/get_open_orders" =>
                    Ok("""{"result":[],"jsonrpc":"2.0"}"""),
                _ => throw new Xunit.Sdk.XunitException("Forbidden endpoint")
            };
        });
        var counts = await new DeribitLiveReadOnlyClient(
            http, "read-only-id", "read-only-secret").ProbeAsync();

        Assert.Equal(new DeribitLiveReadOnlyCounts(2, 1, 0, 1, 0, 0, 0), counts);
        Assert.Equal(3, paths.Count);
        Assert.DoesNotContain(paths, p => p.Contains("secret"));
        Assert.DoesNotContain(paths, p => p.Contains("/private/buy"));
        Assert.DoesNotContain(paths, p => p.Contains("/private/cancel"));
    }

    [Fact]
    public async Task MissingCredentials_RejectsBeforeAnyTransport()
    {
        var hits = 0;
        using var http = NewClient(_ =>
        {
            hits++;
            return Ok("{}");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeribitLiveReadOnlyClient(http, " ", "secret").ProbeAsync());
        Assert.Equal(0, hits);
    }

    [Theory]
    [InlineData("""{"result":[],"jsonrpc":"2.0"}""")]
    [InlineData("""{"result":{"summaries":null},"jsonrpc":"2.0"}""")]
    [InlineData("""{"result":{"summaries":[{}]},"jsonrpc":"2.0"}""")]
    [InlineData("""{"error":{"code":10028},"jsonrpc":"2.0"}""")]
    [InlineData("""{"result":{"summaries":[]},"jsonrpc":"1.0"}""")]
    public async Task InvalidAccountEnvelope_FailsClosedWithoutFurtherRequests(
        string firstResponse)
    {
        var hits = 0;
        using var http = NewClient(_ =>
        {
            hits++;
            return Ok(firstResponse);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewProbe(http).ProbeAsync());
        Assert.Equal(1, hits);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpFailureOrRedirect_NeverRetransmitsCredentials(
        HttpStatusCode status)
    {
        var hits = 0;
        using var http = NewClient(_ =>
        {
            hits++;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent("no trustworthy response")
            };
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewProbe(http).ProbeAsync());
        Assert.Equal(1, hits);
    }

    [Theory]
    [InlineData("positions")]
    [InlineData("orders")]
    public async Task MalformedPositionsOrOrders_FailsClosed(string broken)
    {
        var calls = 0;
        using var http = NewClient(_ =>
        {
            calls++;
            return calls switch
            {
                1 => Ok("""{"jsonrpc":"2.0","result":{"summaries":[]}}"""),
                2 when broken == "positions" =>
                    Ok("""{"jsonrpc":"2.0","result":{}}"""),
                2 => Ok("""{"jsonrpc":"2.0","result":[]}"""),
                _ => Ok("""{"jsonrpc":"2.0","result":{}}""")
            };
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewProbe(http).ProbeAsync());
        Assert.Equal(broken == "positions" ? 2 : 3, calls);
    }

    [Fact]
    public async Task HistoryEndpoint_OnlyReadGetWithBoundedParamsAndBasicAuth()
    {
        var calls = 0;
        using var http = NewClient(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("www.deribit.com", request.RequestUri.Host);
            Assert.Equal("/api/v2/private/get_user_trades_by_currency_and_time",
                request.RequestUri.AbsolutePath);
            Assert.Contains("currency=BTC", request.RequestUri.Query);
            Assert.Contains("start_timestamp=100", request.RequestUri.Query);
            Assert.Contains("end_timestamp=200", request.RequestUri.Query);
            Assert.Contains("count=1000", request.RequestUri.Query);
            Assert.Contains("historical=true", request.RequestUri.Query);
            Assert.Equal(Basic, request.Headers.Authorization!.Scheme);
            Assert.Equal(AuthorizationValue,
                request.Headers.Authorization.Parameter);
            return Ok("""
                {"jsonrpc":"2.0","result":{"has_more":false,"trades":[
                    {"trade_id":"BTC-t1","order_id":"BTC-o1",
                     "instrument_name":"BTC-PERPETUAL","direction":"buy",
                     "amount":100,"price":60000,
                     "fee":0.00001,"fee_currency":"BTC","timestamp":150}
                ]}}
                """);
        });

        var result = await NewProbe(http)
            .CollectTradeHistoryAsync("BTC", 100, 200, historical: true);
        Assert.Equal(1, calls);
        Assert.Equal(1, result.UniqueTrades);
        Assert.Equal(1, result.PageCalls);
        Assert.False(result.HistoricalCoverageProven);
    }

    [Fact]
    public async Task HistoryInvalidCurrency_DoesNotTransmitAnyRequest()
    {
        var calls = 0;
        using var http = NewClient(_ =>
        {
            calls++;
            return Ok("{}");
        });
        await Assert.ThrowsAsync<ArgumentException>(
            () => NewProbe(http).CollectTradeHistoryAsync(
                "../BTC", 100, 200, historical: true));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task HistoryHttpFailure_DoesNotContinueOrReauthenticate()
    {
        var calls = 0;
        using var http = NewClient(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("rate limit")
            };
        });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewProbe(http).CollectTradeHistoryAsync(
                "BTC", 100, 200, historical: false));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TypedPositionRead_OnlyOneMainnetGetAndPreservesNativeSignedSize()
    {
        var calls = 0;
        using var http = NewClient(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("www.deribit.com", request.RequestUri!.Host);
            Assert.Equal("/api/v2/private/get_positions?currency=any",
                request.RequestUri.PathAndQuery);
            Assert.Null(request.Content);
            return Ok("""
                {"jsonrpc":"2.0","result":[
                    {"instrument_name":"BTC-PERPETUAL","kind":"future",
                     "direction":"sell","size":-200,"size_currency":0.004}
                ]}
                """);
        });

        var snapshot = await NewProbe(http).ReadPositionEvidenceAsync();
        Assert.Equal(1, calls);
        Assert.Single(snapshot.Positions);
        Assert.Equal(-200m, snapshot.Positions[0].NativeSize);
        Assert.Equal(0.004m, snapshot.Positions[0].BaseCurrencySize);
        Assert.True(snapshot.ResponseReceivedAt >= snapshot.RequestStartedAt);
    }

    [Fact]
    public async Task TypedPositionRead_InvalidPayloadFailsClosed()
    {
        var calls = 0;
        using var http = NewClient(_ =>
        {
            calls++;
            return Ok("""{"jsonrpc":"2.0","result":{"invalid":true}}""");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewProbe(http).ReadPositionEvidenceAsync());
        Assert.Equal(1, calls);
    }

    private static DeribitLiveReadOnlyClient NewProbe(HttpClient http) =>
        new(http, "read-only-id", "read-only-secret");

    private static HttpClient NewClient(
        Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new Handler(respond));

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class Handler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
