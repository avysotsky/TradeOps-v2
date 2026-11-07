using System.Net;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit.Fix;
using Xunit;

namespace TradeOps.UnitTests;
public sealed class DeribitFixQualificationMarketTests
{
    private sealed class Handler(string variant):HttpMessageHandler
    {
        internal List<string> Methods=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal("test.deribit.com",request.RequestUri!.Host);Assert.Equal("https",request.RequestUri.Scheme);Assert.Null(request.Headers.Authorization);
            using var d=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var r=d.RootElement;var method=r.GetProperty("method").GetString()!;Methods.Add(method);
            Assert.Equal("ETH-PERPETUAL",r.GetProperty("params").GetProperty("instrument_name").GetString());
            object result=method=="public/get_instrument"?new{instrument_name="ETH-PERPETUAL",kind="future",instrument_type=variant=="linear"?"linear":"reversed",
                base_currency="ETH",settlement_currency="ETH",quote_currency="USD",is_active=true,tick_size_steps=variant=="steps"?new[]{new{above_price=1000,tick_size=1}}:[],
                contract_size=1,min_trade_amount=1,tick_size=.5m}:new{instrument_name="ETH-PERPETUAL",state="open",timestamp=DateTimeOffset.UtcNow.AddSeconds(variant=="stale"?-10:0).ToUnixTimeMilliseconds(),
                    best_bid_price=2000,best_ask_price=2001,min_price=1000,max_price=3000};
            return new(HttpStatusCode.OK){RequestMessage=request,Content=new StringContent(JsonSerializer.Serialize(new{jsonrpc="2.0",id=r.GetProperty("id").GetInt64(),result}))};
        }
    }
    [Theory]
    [InlineData("valid")][InlineData("linear")][InlineData("steps")][InlineData("stale")]
    public async Task QuotesComeOnlyFromFixedOriginPublicNativeMetadataAndBoundedBook(string variant)
    {
        using var handler=new Handler(variant);using var http=new HttpClient(handler);
        async Task Read(){var q=await new DeribitFixQualificationMarketV1(DeribitFixGatewayPostgresTests.Binding(),http).ReadAsync(default);Assert.Equal(1800,DeribitFixQualificationScenarioV1.Price(q,DateTimeOffset.UtcNow));}
        if(variant=="valid")await Read();else await Assert.ThrowsAsync<InvalidOperationException>(Read);
        Assert.Equal(new[]{"public/get_instrument","public/get_order_book"},handler.Methods);
    }
    [Fact]
    public void NativeAmendmentValuationHasVersionTwoAndRetainsFullAdditionalRisk()
    {
        var b=DeribitRiskInputsTests.Baseline();var c=DeribitRiskInputsTests.Command() with{ExpectedAggregateVersion=new(1),Payload=DeribitRiskInputsTests.Command().Payload with{
            Kind=OmsJournalCommandKind.Replace,TargetExchangeOrderId="synthetic-native"}};
        c.Validate();
        var risk=DeribitRiskInputsReader.Evaluate(c,b,DeribitRiskInputsTests.Policy(),DeribitRiskInputsTests.Observation(b),DeribitRiskInputsTests.Now)!;
        Assert.NotNull(risk);Assert.Equal(2,risk.Valuation.SchemaVersion);Assert.Equal(10,risk.Valuation.OrderNotionalUsd);Assert.True(risk.Valuation.IncrementalMarginUsd>0);
        Assert.True(OmsRiskLimitsV1.Allows(c,risk.Valuation.StateSha256,risk,10,10,1,DeribitRiskInputsTests.Now));
        Assert.False(OmsRiskLimitsV1.Allows(c,risk.Valuation.StateSha256,risk with{Valuation=risk.Valuation with{SchemaVersion=1}},0,0,0,DeribitRiskInputsTests.Now));
    }
}
