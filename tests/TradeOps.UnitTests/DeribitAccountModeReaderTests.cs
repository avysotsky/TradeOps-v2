using System.Net;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;
using Xunit;

namespace TradeOps.UnitTests;
public sealed class DeribitAccountModeReaderTests
{
    [Theory]
    [InlineData("cross")][InlineData("segregated")][InlineData("no-total")][InlineData("wrong-id")]
    [InlineData("wrong-type")][InlineData("wrong-currency")][InlineData("bad-mode")][InlineData("write-grant")]
    public async Task NativeModeAndDollarTotalAreReadWithoutSelectingOrChangingTheAccount(string mode)
    {
        var scope=new AccountKey(new("tenant"),new("DERIBIT"),OmsEnvironment.Mainnet,new("observed-native:123"),new TradingSubaccountId("observed-native:123"));
        var binding=new DeribitPrivateCaptureBinding(scope,new(){WebSocketUrl="wss://www.deribit.com/ws/api/v2",ClientId="synthetic-client",ClientSecret="synthetic-secret"},
            new(123,true,[new("ETH-PERPETUAL","future","ETH",OmsPrivateAmountUnit.Usd,"USD")]));
        using var handler=new Venue(mode);using var http=new HttpClient(handler);
        Task<DeribitAccountModeObservationV1> Read()=>DeribitAccountModeReaderV1.ReadAsync(binding,http,default);
        if(mode is not("cross" or "segregated" or "no-total")){await Assert.ThrowsAsync<InvalidOperationException>(Read);return;}
        var result=await Read();Assert.Equal("ETH",result.Currency);Assert.Equal(mode=="segregated"?"segregated_sm":"cross_pm",result.MarginModel);
        Assert.Equal(mode!="segregated",result.CrossCollateral);Assert.Equal(mode!="segregated",result.PortfolioMargin);
        Assert.Equal(mode=="no-total"?(decimal?)null:8.92m,result.NativeTotalEquityUsd);Assert.Equal(2,handler.Calls);
        var encoded=JsonSerializer.Serialize(result);Assert.DoesNotContain("synthetic-secret",encoded);Assert.DoesNotContain("synthetic-token",encoded);
    }
    private sealed class Venue(string mode):HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;Assert.Equal("www.deribit.com",request.RequestUri!.Host);Assert.Equal("https",request.RequestUri.Scheme);Assert.Empty(request.RequestUri.Query);
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var root=body.RootElement;var p=root.GetProperty("params");object result;
            if(root.GetProperty("method").GetString()=="public/auth")
            {Assert.Null(request.Headers.Authorization);var scope=p.GetProperty("scope").GetString()!;Assert.DoesNotContain("read_write",scope);result=new{access_token="synthetic-token",expires_in=300,scope=mode=="write-grant"?scope.Replace("account:read","account:read_write"):scope};}
            else
            {
                Assert.Equal("private/get_account_summary",root.GetProperty("method").GetString());Assert.Equal("synthetic-token",request.Headers.Authorization?.Parameter);
                Assert.Equal("ETH",p.GetProperty("currency").GetString());Assert.True(p.GetProperty("extended").GetBoolean());Assert.Equal(2,p.EnumerateObject().Count());
                result=new{id=mode=="wrong-id"?999:123,type=mode=="wrong-type"?"main":"subaccount",currency=mode=="wrong-currency"?"BTC":"ETH",
                    margin_model=mode=="bad-mode"?"cross\ninvalid":mode=="segregated"?"segregated_sm":"cross_pm",cross_collateral_enabled=mode!="segregated",portfolio_margining_enabled=mode!="segregated",
                    balance=.003m,equity=.0031m,available_funds=.002m,total_equity_usd=mode=="no-total"?(decimal?)null:8.92m};
            }
            return new(HttpStatusCode.OK){RequestMessage=request,Content=new StringContent(JsonSerializer.Serialize(new{jsonrpc="2.0",id=root.GetProperty("id").GetInt64(),result}))};
        }
    }
}
