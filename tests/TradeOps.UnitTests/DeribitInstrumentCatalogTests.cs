using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitInstrumentCatalogTests
{
    [Theory]
    [InlineData("future", "reversed", "USD", "BTC", OmsPrivateAmountUnit.Usd, "USD")]
    [InlineData("future", "linear", "USDC", "USDC", OmsPrivateAmountUnit.BaseCoin, "BTC")]
    [InlineData("option", "reversed", "BTC", "BTC", OmsPrivateAmountUnit.BaseCoin, "BTC")]
    [InlineData("option", "linear", "USDC", "USDC", OmsPrivateAmountUnit.BaseCoin, "BTC")]
    public async Task NativeAmount_UsesMetadataAndKind_NotContractSizeOrSymbol(string kind, string type,
        string quote, string settle, OmsPrivateAmountUnit unit, string amountCurrency)
    {
        var item = Item(kind: kind, type: type, quote: quote, settle: settle);
        item["instrument_name"] = "NATIVE-NAME-WITHOUT-BASE-HINT";
        item["contract_size"] = 1000m;
        using var http = Client(_ => Ok(Body(item)));
        var snapshot = await new DeribitInstrumentCatalog(http).ReadAsync(OmsEnvironment.Mainnet, "BTC", kind);
        var actual = Assert.Single(snapshot.Instruments);
        Assert.Equal(unit, actual.AmountUnit);
        Assert.Equal(amountCurrency, actual.AmountCurrency);
        Assert.Equal("BTC", actual.BaseCurrency);
        Assert.Equal(settle, actual.SettlementCurrency);
    }

    [Theory]
    [InlineData(OmsEnvironment.Mainnet, "www.deribit.com")]
    [InlineData(OmsEnvironment.Testnet, "test.deribit.com")]
    public async Task Query_IsOneCredentialFreeFixedOriginGet(OmsEnvironment environment, string host)
    {
        var calls = 0;
        using var http = Client(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"https://{host}/api/v2/public/get_instruments?currency=BTC&kind=future&expired=false", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Content);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
            return Ok(Body(Item()));
        });
        http.BaseAddress = new Uri("https://untrusted.invalid/");
        Assert.Single((await new DeribitInstrumentCatalog(http).ReadAsync(environment, "BTC", "future")).Instruments);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("missing-base")]
    [InlineData("wrong-kind")]
    [InlineData("unknown-type")]
    [InlineData("inverse-non-usd")]
    [InlineData("duplicate")]
    [InlineData("bad-json")]
    [InlineData("rpc-error")]
    [InlineData("wrong-version")]
    public async Task InvalidMetadata_FailsWholeSnapshot_WithoutRawPayload(string scenario)
    {
        var item = Item();
        if (scenario == "missing-base") item.Remove("base_currency");
        if (scenario == "wrong-kind") item["kind"] = "option";
        if (scenario == "unknown-type") item["instrument_type"] = "unqualified";
        if (scenario == "inverse-non-usd") item["quote_currency"] = "EUR";
        var body = scenario switch
        {
            "duplicate" => Body(item, item), "bad-json" => "synthetic-sensitive-payload",
            "rpc-error" => """{"jsonrpc":"2.0","error":{"message":"synthetic-sensitive-payload"}}""",
            "wrong-version" => Body(item).Replace("2.0", "1.0"), _ => Body(item)
        };
        using var http = Client(_ => Ok(body));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeribitInstrumentCatalog(http).ReadAsync(OmsEnvironment.Mainnet, "BTC", "future"));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic-sensitive", error.ToString());
    }

    [Fact]
    public async Task EmptyCatalog_IsValid_AndInactiveMetadataIsRetainedForLateEvents()
    {
        var inactive = Item(); inactive["is_active"] = false;
        var calls = 0;
        using var http = Client(_ => Ok(calls++ == 0 ? Body() : Body(inactive)));
        var catalog = new DeribitInstrumentCatalog(http);
        Assert.Empty((await catalog.ReadAsync(OmsEnvironment.Mainnet, "NEW_COIN", "future")).Instruments);
        Assert.False(Assert.Single((await catalog.ReadAsync(OmsEnvironment.Mainnet, "BTC", "future")).Instruments).IsActive);
    }

    [Theory]
    [InlineData("currency")]
    [InlineData("kind")]
    [InlineData("environment")]
    [InlineData("authorization")]
    [InlineData("cookie")]
    public async Task InvalidQueryOrCredentialBearingClient_IsRejectedBeforeNetwork(string scenario)
    {
        using var http = Client(_ => throw new Xunit.Sdk.XunitException("Network must not be called."));
        if (scenario == "authorization") http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic");
        if (scenario == "cookie") http.DefaultRequestHeaders.Add("Cookie", "synthetic=value");
        await Assert.ThrowsAsync<ArgumentException>(() => new DeribitInstrumentCatalog(http).ReadAsync(
            scenario == "environment" ? OmsEnvironment.Paper : OmsEnvironment.Mainnet,
            scenario == "currency" ? "BTC&redirect=bad" : "BTC", scenario == "kind" ? "future_combo" : "future"));
        if (scenario == "currency")
            await Assert.ThrowsAsync<ArgumentException>(() => new DeribitInstrumentCatalog(http).ReadAsync(OmsEnvironment.Mainnet, "ANY", "future"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedResponse_IsRejected_WithOrWithoutContentLength(bool declared)
    {
        using var http = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new TestContent(new byte[8 * 1024 * 1024 + 1], declared) });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DeribitInstrumentCatalog(http).ReadAsync(OmsEnvironment.Mainnet, "BTC", "future"));
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task CallerCancellation_IsPreserved()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        using var http = Client(_ => throw new OperationCanceledException(stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DeribitInstrumentCatalog(http).ReadAsync(OmsEnvironment.Mainnet, "BTC", "future", stop.Token));
    }

    [Fact]
    public async Task PlanValidation_RequiresMatchingUnitsNamesEnvironmentAndUniqueQueryGroup()
    {
        using var http = Client(_ => Ok(Body(Item())));
        var snapshot = await new DeribitInstrumentCatalog(http).ReadAsync(OmsEnvironment.Mainnet, "BTC", "future");
        DeribitPrivateCapturePlan Plan(string name = "BTC-PERPETUAL", OmsPrivateAmountUnit unit = OmsPrivateAmountUnit.Usd) =>
            new(123, false, new[] { new DeribitPrivateInstrument(name, "future", "BTC", unit,
                unit == OmsPrivateAmountUnit.Usd ? "USD" : "BTC") });
        DeribitInstrumentCatalog.ValidatePlan(OmsEnvironment.Mainnet, Plan(), new[] { snapshot });
        Assert.Throws<InvalidOperationException>(() => DeribitInstrumentCatalog.ValidatePlan(OmsEnvironment.Testnet, Plan(), new[] { snapshot }));
        Assert.Throws<InvalidOperationException>(() => DeribitInstrumentCatalog.ValidatePlan(OmsEnvironment.Mainnet, Plan("missing"), new[] { snapshot }));
        Assert.Throws<InvalidOperationException>(() => DeribitInstrumentCatalog.ValidatePlan(OmsEnvironment.Mainnet, Plan(unit: OmsPrivateAmountUnit.BaseCoin), new[] { snapshot }));
        Assert.Throws<InvalidOperationException>(() => DeribitInstrumentCatalog.ValidatePlan(OmsEnvironment.Mainnet, Plan(), new[] { snapshot, snapshot }));
        Assert.Throws<NotSupportedException>(() => ((IList<DeribitInstrumentMetadata>)snapshot.Instruments).Clear());
    }

    private static Dictionary<string, object> Item(string kind = "future", string type = "reversed", string quote = "USD", string settle = "BTC") => new()
    {
        ["instrument_name"] = "BTC-PERPETUAL", ["kind"] = kind, ["instrument_type"] = type,
        ["base_currency"] = "BTC", ["quote_currency"] = quote, ["settlement_currency"] = settle, ["is_active"] = true
    };
    private static string Body(params Dictionary<string, object>[] items) => JsonSerializer.Serialize(new { jsonrpc = "2.0", result = items });
    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> reply) => new(new Handler(reply));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(reply(request));
    }
    private sealed class TestContent(byte[] bytes, bool declared) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = bytes.Length; return declared; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, false));
    }
}
