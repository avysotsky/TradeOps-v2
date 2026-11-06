using System.Net;
using System.Text;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;
public sealed class DeribitRiskObservationReaderTests
{
    [Theory]
    [InlineData("ok")] [InlineData("duplicate-page")]
    public async Task ActualReadPathUsesNativePinCompleteUnfilteredDayLogAndNoProfitOffsets(string mode)
    {
        using var server=new Server(mode);using var http=new HttpClient(server);var b=DeribitRiskInputsTests.Baseline() with{Scope=Scope()};
        var c=DeribitRiskInputsTests.Command() with{Scope=Scope()};var reader=new DeribitRiskObservationReader(new(Scope(),Options(),Plan()),http,new Clock(),new Pending());
        var observation=await reader.ReadAsync(c,b);Assert.NotNull(observation);
        Assert.Equal(.033m,Assert.Single(observation.Currencies).DailyDebitBound);Assert.Equal(.01m,observation.BuyMargin);
        Assert.Contains("private/get_transaction_log",server.Methods);Assert.DoesNotContain("private/buy",server.Methods);
        Assert.Equal(mode=="ok"?1:2,server.Pages);
    }
    [Theory]
    [InlineData("wrong-account")] [InlineData("loop")] [InlineData("missing-change")] [InlineData("stale-ticker")] [InlineData("margin-error")]
    public async Task IncompleteNativeRiskInputsReturnNoValuation(string mode)
    {
        using var server=new Server(mode);using var http=new HttpClient(server);var b=DeribitRiskInputsTests.Baseline() with{Scope=Scope()};
        Assert.Null(await new DeribitRiskObservationReader(new(Scope(),Options(),Plan()),http,new Clock(),new Pending()).ReadAsync(DeribitRiskInputsTests.Command() with{Scope=Scope()},b));
    }
    private sealed class Clock:TimeProvider{public override DateTimeOffset GetUtcNow()=>DeribitRiskInputsTests.Now;}
    private sealed class Pending(bool populated=false):IOmsPendingRiskReader
    {public Task<IReadOnlyList<OmsPendingNativeRiskV1>> ReadAsync(TradeOps.Domain.Oms.AccountKey scope,TradeOps.Domain.Oms.OmsCommandId except,CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<OmsPendingNativeRiskV1>>(populated?[new("ETH-PERPETUAL",20,1990)]:[]);}
    [Fact]
    public async Task HypotheticalMarginUsesTheWholePendingNativeQuantityAndWorstPrice()
    {
        using var server=new Server("pending");using var http=new HttpClient(server);var b=DeribitRiskInputsTests.Baseline() with{Scope=Scope()};
        var observation=await new DeribitRiskObservationReader(new(Scope(),Options(),Plan()),http,new Clock(),new Pending(true)).ReadAsync(DeribitRiskInputsTests.Command() with{Scope=Scope()},b);
        Assert.NotNull(observation);Assert.Equal(30,Assert.Single(observation.MarginBounds!).NativeUsd);
    }
    internal sealed class Server(string mode,Func<DateTimeOffset>? clock=null):HttpMessageHandler
    {
        internal readonly List<string> Methods=[];internal int Pages;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            using var d=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var r=d.RootElement;var p=r.GetProperty("params");var method=r.GetProperty("method").GetString()!;Methods.Add(method);
            Assert.Equal("test.deribit.com",request.RequestUri!.Host);object result;
            var now=clock?.Invoke()??DeribitRiskInputsTests.Now;
            switch(method)
            {
                case "public/auth":Assert.DoesNotContain("read_write",p.GetProperty("scope").GetString());result=new{access_token="scripted",scope=p.GetProperty("scope").GetString(),expires_in=300};break;
                case "public/get_time":result=now.ToUnixTimeMilliseconds();break;
                case "public/get_instrument":var coin=p.GetProperty("instrument_name").GetString()!.StartsWith("BTC",StringComparison.Ordinal)?"BTC":"ETH";
                    result=new{instrument_name=p.GetProperty("instrument_name").GetString(),kind="future",instrument_type="reversed",base_currency=coin,quote_currency="USD",settlement_currency=coin,is_active=true,expiration_timestamp=now.AddYears(3).ToUnixTimeMilliseconds(),min_trade_amount=1,tick_size=.5m,tick_size_steps=Array.Empty<object>()};break;
                case "public/ticker":var mark=p.GetProperty("instrument_name").GetString()!.StartsWith("BTC",StringComparison.Ordinal)?80000:2000;
                    result=new{instrument_name=p.GetProperty("instrument_name").GetString(),timestamp=now.AddSeconds(mode=="stale-ticker"?-10:0).ToUnixTimeMilliseconds(),mark_price=mark,index_price=mark};break;
                case "private/get_account_summary":var currency=p.GetProperty("currency").GetString()!;var funded=currency=="ETH";
                    result=new{id=mode=="wrong-account"?999:123,type="main",currency,balance=funded?1:0,equity=funded?1:0,margin_balance=funded?1:0,available_funds=funded ? .9m : 0,cross_collateral_enabled=false,portfolio_margining_enabled=false,margin_model="segregated_sm",initial_margin=funded ? .1m : 0,projected_initial_margin=funded ? .1m : 0,session_rpl=0,session_upl=0};break;
                case "private/get_transaction_log":
                    Pages++;Assert.False(p.TryGetProperty("query",out _));Assert.Equal(now.UtcDateTime.Date,DateTimeOffset.FromUnixTimeMilliseconds(p.GetProperty("start_timestamp").GetInt64()).UtcDateTime);
                    var log=new Dictionary<string,object?> { ["id"]=1,["user_id"]=123,["currency"]="ETH",["timestamp"]=now.AddHours(-1).ToUnixTimeMilliseconds(),["change"]=-.01m,["cashflow"]=-.02m,["commission"]=.003m };
                    if(mode=="missing-change")log.Remove("change");
                    result=new{logs=p.GetProperty("currency").GetString()=="ETH"?new[]{log}:Array.Empty<Dictionary<string,object?>>(),continuation=mode=="loop"||mode=="duplicate-page"&&Pages==1?(long?)11:null};break;
                case "private/get_margins":
                    if(mode=="pending"){Assert.Equal(30,p.GetProperty("amount").GetDecimal());Assert.Equal(1990,p.GetProperty("price").GetDecimal());}
                    if(mode=="margin-error")return Response(request,new{jsonrpc="2.0",id=r.GetProperty("id").GetInt64(),error=new{code=10000,message="scripted"}});
                    result=new{buy=.01m,sell=.02m,buy_taker_fee=.001m,sell_taker_fee=.001m,buy_maker_fee=0,sell_maker_fee=0,min_price=1700,max_price=2300};break;
                default:throw new InvalidOperationException("Unexpected native risk RPC.");
            }
            return Response(request,new{jsonrpc="2.0",id=r.GetProperty("id").GetInt64(),result});
        }
        private static HttpResponseMessage Response(HttpRequestMessage request,object value)=>new(HttpStatusCode.OK){RequestMessage=request,Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    }
}
