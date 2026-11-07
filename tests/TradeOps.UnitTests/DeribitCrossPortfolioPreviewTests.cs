using System.Net;
using System.Text.Json;
using TradeOps.Connectors.Deribit;
using Xunit;

namespace TradeOps.UnitTests;
public sealed class DeribitCrossPortfolioPreviewTests
{
    [Theory]
    [InlineData("valid")][InlineData("scope")][InlineData("mode")][InlineData("grant")][InlineData("contract")]
    [InlineData("minimum")][InlineData("indices")][InlineData("missing-usd")][InlineData("simulation-mode")]
    [InlineData("negative-margin")][InlineData("cash-change")][InlineData("venue-error")]
    public async Task WholePortfolioPreviewUsesNativeUsdUnitsWithoutCreatingAnyOrder(string mode)
    {
        var input=FixHostAccountInspectionTests.Input();using var venue=new Venue(mode);using var http=new HttpClient(venue);
        var binding=TradeOps.FixHostPreparation.AccountInspection.Binding(input);
        Task<DeribitCrossPortfolioPreviewV1> Read()=>DeribitCrossPortfolioPreviewReaderV1.ReadAsync(binding,http,[-1m,0,1],default);
        if(mode!="valid")
        {
            var error=await Assert.ThrowsAsync<DeribitCrossPortfolioReadException>(Read);
            Assert.Equal(mode switch{"scope" or "mode"=>"NativeAccount","grant"=>"Authentication","contract" or "minimum"=>"ContractUnits","indices"=>"NativeRiskMatrix","cash-change"=>"NativeAccountRecheck",_=>"NativeSimulation"},error.Stage);
            Assert.Equal(mode=="venue-error"?-32602:(int?)null,error.VenueCode);return;
        }
        var result=await Read();Assert.False(result.AccountReady||result.TradingReady);Assert.Equal(8.92m,result.Before.EquityUsd);
        Assert.Equal(new[]{-1m,0,1},result.Scenarios.Select(x=>x.NativeUsdChange));Assert.Equal(3,venue.Simulations);Assert.Equal(2,venue.Summaries);
        Assert.Equal(.49m,result.Scenarios.Min(x=>x.Totals.InitialMarginUsd));Assert.Equal(.51m,result.Scenarios.Max(x=>x.Totals.InitialMarginUsd));
        var serialized=JsonSerializer.Serialize(result);Assert.DoesNotContain("synthetic-secret",serialized);Assert.DoesNotContain("synthetic-token",serialized);
    }
    [Theory]
    [InlineData("empty")][InlineData("many")][InlineData("duplicate")][InlineData("no-zero")][InlineData("fraction")][InlineData("oversize")]
    public async Task ScenarioCapacityAndNativeUnitsAreCheckedBeforeAuthentication(string mode)
    {
        decimal[] changes=mode switch{"empty"=>[],"many"=>[-4,-3,-2,-1,0,1,2,3],"duplicate"=>[0,0],"no-zero"=>[1],"fraction"=>[0,.5m],_=>[0,4]};
        using var venue=new Venue("");using var http=new HttpClient(venue);
        await Assert.ThrowsAsync<ArgumentException>(()=>DeribitCrossPortfolioPreviewReaderV1.ReadAsync(TradeOps.FixHostPreparation.AccountInspection.Binding(FixHostAccountInspectionTests.Input()),http,changes,default));
        Assert.Equal(0,venue.Calls);
    }
    private sealed class Venue(string mode):HttpMessageHandler
    {
        internal int Calls,Simulations,Summaries;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;Assert.Equal("www.deribit.com",request.RequestUri!.Host);Assert.Equal("https",request.RequestUri.Scheme);Assert.Empty(request.RequestUri.Query);
            using var doc=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var root=doc.RootElement;var p=root.GetProperty("params");object result;var method=root.GetProperty("method").GetString();
            Assert.Equal("/api/v2/"+method,request.RequestUri.AbsolutePath);
            if(method=="public/auth")
            {Assert.Null(request.Headers.Authorization);var scope=p.GetProperty("scope").GetString()!;Assert.DoesNotContain("read_write",scope);result=new{access_token="synthetic-token",expires_in=300,scope=mode=="grant"?scope.Replace("account:read","account:read_write"):scope};}
            else if(method=="public/get_instrument")
            {Assert.Equal("ETH-PERPETUAL",p.GetProperty("instrument_name").GetString());result=new{instrument_name="ETH-PERPETUAL",kind="future",instrument_type="reversed",base_currency="ETH",quote_currency="USD",settlement_currency="ETH",is_active=true,contract_size=mode=="contract"?10:1,min_trade_amount=mode=="minimum"?10:1};}
            else
            {
                Assert.Equal("synthetic-token",request.Headers.Authorization?.Parameter);
                if(method=="private/pme/simulate")
                {Assert.Equal("CROSS",p.GetProperty("currency").GetString());Assert.Single(p.EnumerateObject());result=new{index_price=new Dictionary<string,decimal>{{"eth_usd",mode=="indices"?-1:2500},{"btc_usd",80000}}};}
                else
                {
                    decimal delta=0;
                    if(method=="private/get_account_summary"){Summaries++;Assert.Equal("ETH",p.GetProperty("currency").GetString());Assert.True(p.GetProperty("extended").GetBoolean());Assert.Equal(2,p.EnumerateObject().Count());}
                    else
                    {
                        Assert.Equal("private/simulate_portfolio",method);Simulations++;Assert.Equal("ETH",p.GetProperty("currency").GetString());Assert.True(p.GetProperty("add_positions").GetBoolean());
                        var positions=p.GetProperty("simulated_positions");Assert.Single(positions.EnumerateObject());delta=positions.GetProperty("ETH-PERPETUAL").GetDecimal();Assert.True(delta is >=-1 and <=1);
                    }
                    var values=new Dictionary<string,object>{["id"]=mode=="scope"?999:123,["type"]="subaccount",["currency"]="ETH",["margin_model"]=mode=="mode"||mode=="simulation-mode"&&Simulations>0?"segregated_sm":"cross_pm",
                        ["cross_collateral_enabled"]=true,["portfolio_margining_enabled"]=true,["total_equity_usd"]=8.92m,["total_margin_balance_usd"]=8.5m,
                        ["total_initial_margin_usd"]=mode=="negative-margin"&&Simulations>0?-1m:.5m+delta*.01m,["total_maintenance_margin_usd"]=.2m,["projected_initial_margin"]=.0002m,
                        ["balance"]=mode=="cash-change"&&Summaries>1?.004m:.003m};
                    if(mode=="missing-usd"&&Simulations>0)values.Remove("total_initial_margin_usd");result=values;
                }
            }
            var json=method=="private/simulate_portfolio"&&mode=="venue-error"?JsonSerializer.Serialize(new{jsonrpc="2.0",id=root.GetProperty("id").GetInt64(),error=new{code=-32602}}):
                JsonSerializer.Serialize(new{jsonrpc="2.0",id=root.GetProperty("id").GetInt64(),result});
            return new(method=="private/simulate_portfolio"&&mode=="venue-error"?HttpStatusCode.BadRequest:HttpStatusCode.OK){RequestMessage=request,Content=new StringContent(json)};
        }
    }
}
