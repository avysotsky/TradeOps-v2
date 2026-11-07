using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;
using TradeOps.FixHostPreparation;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;
public sealed class DeribitCrossPortfolioRiskObservationTests
{
    private static readonly DateTimeOffset Now=DeribitRiskInputsTests.Now;
    [Theory]
    [InlineData("valid",0)][InlineData("valid",2)][InlineData("wrong-account",0)][InlineData("unknown-funded",0)]
    public async Task HostDiagnosticUsesNativeInventoryAndPublicUnitsWithoutCreatingLiveAuthority(string mode,int hypotheticalPending)
    {
        using var venue=new Venue(mode,true);using var http=new HttpClient(venue);var input=FixHostAccountInspectionTests.Input();input.Purpose=hypotheticalPending==0?"READ_ONLY_FIX_PM_RISK_OBSERVATION_V2":"READ_ONLY_FIX_PM_STRESS_OBSERVATION_V2";
        var result=await PmRiskInspection.CollectAsync(input,http,default,hypotheticalPending);
        Assert.True(result.DiagnosticOnly);Assert.False(result.AccountReady||result.TradingReady||result.DatabaseUsed);
        Assert.Equal("diagnostic-unqualified-v2",result.InventoryReference.Policy);Assert.Single(result.InventoryReference.Positions);
        Assert.Equal(mode=="valid",result.Observation is not null);
        Assert.Equal(hypotheticalPending,result.HypotheticalPendingNativeUsd);
        if(mode=="valid")Assert.Equal(hypotheticalPending==0?3:7,result.Observation!.Scenarios.Count);
        Assert.DoesNotContain("synthetic-secret",JsonSerializer.Serialize(result));Assert.DoesNotContain("synthetic-token",JsonSerializer.Serialize(result));
    }
    [Theory]
    [InlineData("valid")][InlineData("pending")][InlineData("cash-change")][InlineData("wrong-account")]
    [InlineData("duplicate-currency")][InlineData("unknown-funded")][InlineData("log-loop")][InlineData("log-collision")]
    [InlineData("bad-log-account")][InlineData("missing-usd")][InlineData("bad-mode")][InlineData("stale-ticker")]
    [InlineData("bad-grant")][InlineData("bad-indices")]
    public async Task BoundedNativeObservationsRetainWholeCollateralAndNeverRequestOrderWrites(string mode)
    {
        using var venue=new Venue(mode);using var http=new HttpClient(venue);var b=DeribitCrossPortfolioRiskTests.Baseline() with{Scope=Scope()};
        var c=DeribitCrossPortfolioRiskTests.Command() with{Scope=Scope()};
        var reader=new DeribitCrossPortfolioRiskObservationReaderV2(new(Scope(),Options(),Plan()),http,new Pending(mode=="pending"),new Clock());
        var observation=await reader.ReadAsync(c,b);
        Assert.All(venue.Methods,m=>Assert.DoesNotContain(m,new[]{"private/buy","private/sell","private/edit","private/cancel","private/cancel_all"}));
        Assert.Equal(0,venue.Active);
        if(mode is not("valid" or "pending")){Assert.True(observation is null||DeribitCrossPortfolioRiskV2.Evaluate(c,b,DeribitCrossPortfolioRiskTests.Policy() with{Limits=DeribitCrossPortfolioRiskTests.Policy().Limits with{Scope=Scope()}},observation,Now) is null);return;}
        Assert.NotNull(observation);Assert.Equal(3,observation.Balances.Count);Assert.Contains(observation.Balances,x=>x.Currency=="BUIDL");
        Assert.Equal(mode=="pending"?5:3,observation.Scenarios.Count);
        Assert.Equal(5,venue.LogCurrencies.Distinct().Count());Assert.Equal(mode=="pending"?2:1,venue.FeeAmount);
        Assert.Equal(mode=="pending"?1700:1750,venue.FeePrice);Assert.Equal(.000013m,observation.DailyDebits.Single(x=>x.Currency=="ETH").NegativeCashflowBound);
        Assert.Equal(.00001m,observation.DailyDebits.Single(x=>x.Currency=="ETH").NegativeSessionPnlBound);
        Assert.Equal(.000002m,observation.FeeBoundEth);
        var policy=DeribitCrossPortfolioRiskTests.Policy() with{Limits=DeribitCrossPortfolioRiskTests.Policy().Limits with{Scope=Scope()}};
        Assert.NotNull(DeribitCrossPortfolioRiskV2.Evaluate(c,b,policy,observation,Now));
        Assert.DoesNotContain("synthetic-token",OmsRecoveryRunCodec.Encode(observation).Json);
    }
    [Fact]
    public async Task InvalidPendingCapacityDoesNotAuthenticateAndCancellationPropagates()
    {
        using var venue=new Venue("valid");using var http=new HttpClient(venue);var b=DeribitCrossPortfolioRiskTests.Baseline() with{Scope=Scope()};var c=DeribitCrossPortfolioRiskTests.Command() with{Scope=Scope()};
        var reader=new DeribitCrossPortfolioRiskObservationReaderV2(new(Scope(),Options(),Plan()),http,new Pending(false,3),new Clock());
        Assert.Null(await reader.ReadAsync(c,b));Assert.Empty(venue.Methods);
        using var stop=new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new DeribitCrossPortfolioRiskObservationReaderV2(new(Scope(),Options(),Plan()),http,new Pending(),new Clock()).ReadAsync(c,b,stop.Token));
    }
    private sealed class Clock:TimeProvider{public override DateTimeOffset GetUtcNow()=>Now;}
    private sealed class Pending(bool populated=false,int? count=null):IOmsPendingRiskReader
    {public Task<IReadOnlyList<OmsPendingNativeRiskV1>> ReadAsync(AccountKey s,OmsCommandId c,CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult<IReadOnlyList<OmsPendingNativeRiskV1>>(Enumerable.Range(0,count??(populated?1:0)).Select(_=>new OmsPendingNativeRiskV1("ETH-PERPETUAL",1,1700)).ToArray());}}
    private sealed class Venue(string mode,bool diagnostic=false):HttpMessageHandler
    {
        internal readonly ConcurrentBag<string> Methods=[],LogCurrencies=[];internal int Active;private int all;
        internal decimal FeeAmount,FeePrice;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Interlocked.Increment(ref Active);
            try
            {
                if(diagnostic&&request.Method==HttpMethod.Get)
                {
                    Assert.Equal("https://www.deribit.com/api/v2/public/get_instruments?currency=ETH&kind=future&expired=false",request.RequestUri!.AbsoluteUri);
                    return new(HttpStatusCode.OK){RequestMessage=request,Content=new StringContent(JsonSerializer.Serialize(new{jsonrpc="2.0",result=new[]{new{instrument_name="ETH-PERPETUAL",kind="future",instrument_type="reversed",base_currency="ETH",quote_currency="USD",settlement_currency="ETH",is_active=true}}}))};
                }
                using var doc=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var root=doc.RootElement;var p=root.GetProperty("params");var method=root.GetProperty("method").GetString()!;Methods.Add(method);
                var observedNow=diagnostic?DateTimeOffset.UtcNow:Now;
                Assert.Equal(diagnostic?"www.deribit.com":"test.deribit.com",request.RequestUri!.Host);Assert.Equal("https",request.RequestUri.Scheme);Assert.Empty(request.RequestUri.Query);
                Assert.Equal("/api/v2/"+method,request.RequestUri.AbsolutePath);object result;
                if(method.StartsWith("private/",StringComparison.Ordinal))Assert.Equal("synthetic-token",request.Headers.Authorization?.Parameter);
                switch(method)
                {
                    case "public/auth":var scope=p.GetProperty("scope").GetString()!;Assert.DoesNotContain("read_write",scope);result=new{access_token="synthetic-token",expires_in=300,scope=mode=="bad-grant"?scope.Replace("account:read","account:read_write"):scope};break;
                    case "public/get_time":result=observedNow.ToUnixTimeMilliseconds();break;
                    case "private/get_positions":result=new[]{new{user_id=123,instrument_name="ETH-PERPETUAL",kind="future",size=1,direction="buy",size_currency=.0005m}};break;
                    case "private/get_open_orders":result=Array.Empty<object>();break;
                    case "private/get_account_summary":result=Capital();break;
                    case "private/get_account_summaries":
                        var sequence=Interlocked.Increment(ref all);var funded=mode=="unknown-funded"?.1m:0;
                        var rows=new List<object>{Balance("ETH",mode=="cash-change"&&sequence>1?.005m:.004m),Balance("BTC",0),Balance("BUIDL",funded)};
                        if(mode=="duplicate-currency")rows.Add(Balance("ETH",.004m));result=new{id=123,type=diagnostic?"subaccount":"main",summaries=rows};break;
                    case "public/get_instrument":result=new{instrument_name=p.GetProperty("instrument_name").GetString(),kind="future",instrument_type="reversed",base_currency="ETH",quote_currency="USD",settlement_currency="ETH",is_active=true,
                        expiration_timestamp=Now.AddYears(3).ToUnixTimeMilliseconds(),min_trade_amount=1,contract_size=1,tick_size=.5m,tick_size_steps=Array.Empty<object>()};break;
                    case "public/ticker":result=new{instrument_name=p.GetProperty("instrument_name").GetString(),mark_price=2000,index_price=2000,best_bid_price=1999,best_ask_price=2001,timestamp=observedNow.AddSeconds(mode=="stale-ticker"?-10:0).ToUnixTimeMilliseconds()};break;
                    case "public/get_order_book":result=new{instrument_name="ETH-PERPETUAL",state="open",timestamp=observedNow.ToUnixTimeMilliseconds(),best_bid_price=2000,best_ask_price=2001,min_price=1980,max_price=2020};break;
                    case "private/pme/simulate":Assert.Equal("CROSS",p.GetProperty("currency").GetString());result=new{index_price=new Dictionary<string,decimal>{{"eth_usd",mode=="bad-indices"?-1:2000}}};break;
                    case "private/simulate_portfolio":Assert.Equal("ETH",p.GetProperty("currency").GetString());Assert.True(p.GetProperty("add_positions").GetBoolean());var change=p.GetProperty("simulated_positions").GetProperty("ETH-PERPETUAL").GetInt32();Assert.InRange(change,-3,3);result=Capital();break;
                    case "private/get_margins":FeeAmount=p.GetProperty("amount").GetDecimal();FeePrice=p.GetProperty("price").GetDecimal();result=new{buy=-.1m,sell=-.2m,buy_taker_fee=.000001m,sell_taker_fee=.000002m,buy_maker_fee=-.000001m,sell_maker_fee=0,min_price=1600,max_price=2400};break;
                    case "private/get_transaction_log":
                        var currency=p.GetProperty("currency").GetString()!;LogCurrencies.Add(currency);Assert.False(p.TryGetProperty("query",out _));
                        var row=new{id=1,user_id=mode=="bad-log-account"?999:123,currency,timestamp=diagnostic?p.GetProperty("end_timestamp").GetInt64():Now.AddHours(-1).ToUnixTimeMilliseconds(),change=-.000003m,cashflow=-.000009m,commission=.000001m};
                        object[] logs=currency=="ETH"?[row]:[];
                        if(mode=="log-collision"&&currency=="ETH")logs=[row,new{id=1,user_id=123,currency,timestamp=Now.AddHours(-1).ToUnixTimeMilliseconds(),change=99,cashflow=0,commission=0}];
                        result=new{logs,continuation=mode=="log-loop"&&currency=="ETH"?(long?)1:null};break;
                    default:throw new InvalidOperationException("Unexpected native observation RPC.");
                }
                return new(HttpStatusCode.OK){RequestMessage=request,Content=new StringContent(JsonSerializer.Serialize(new{jsonrpc="2.0",id=root.GetProperty("id").GetInt64(),result}))};
            }
            finally{Interlocked.Decrement(ref Active);}
        }
        private object Balance(string currency,decimal cash)=>new{currency,balance=cash,equity=cash,cross_collateral_enabled=true,portfolio_margining_enabled=true,session_rpl=currency=="ETH"?-.00001m:0,session_upl=currency=="ETH"?.00002m:0};
        private Dictionary<string,object> Capital()
        {
            var row=new Dictionary<string,object>{{"id",mode=="wrong-account"?999:123},{"type",diagnostic?"subaccount":"main"},{"currency","ETH"},{"margin_model",mode=="bad-mode"?"segregated_sm":"cross_pm"},
                {"cross_collateral_enabled",true},{"portfolio_margining_enabled",true},{"total_equity_usd",8},{"total_margin_balance_usd",8},{"total_initial_margin_usd",.2m},{"total_maintenance_margin_usd",.1m},{"projected_initial_margin",.0001m}};
            if(mode=="missing-usd")row.Remove("total_equity_usd");return row;
        }
    }
}
