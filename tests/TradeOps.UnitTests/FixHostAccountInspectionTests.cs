using System.Net;
using System.Text.Json;
using TradeOps.FixHostPreparation;
using Xunit;

namespace TradeOps.UnitTests;
public sealed class FixHostAccountInspectionTests
{
    internal static AccountInput Input()=>new(){Purpose="READ_ONLY_FIX_ACCOUNT_INVENTORY_V1",Environment="Mainnet",Tenant="synthetic-tenant",
        ExpectedAccountId=123,AccountType="subaccount",ClientId="synthetic-client",ClientSecret="synthetic-secret"};
    [Theory]
    [InlineData("valid")][InlineData("wrong-owner")][InlineData("wrong-type")][InlineData("write-grant")]
    [InlineData("unsupported-asset")][InlineData("cross")][InlineData("portfolio")]
    public async Task RealConnectorRetainsNativeInventoryWithoutTradingOrInventingReadiness(string mode)
    {
        using var venue=new Venue(mode);using var http=new HttpClient(venue);var result=await AccountInspection.CollectAsync(Input(),http,default);
        var accepted=mode is not("wrong-owner" or "wrong-type" or "write-grant");Assert.Equal(accepted,result.Diagnostics.NativeBindingVerified);
        Assert.False(result.Diagnostics.TradingStarted||result.Diagnostics.DatabaseUsed||result.Diagnostics.AccountReady);
        if(!accepted){Assert.Null(result.Inventory);Assert.Null(result.Diagnostics.EstimatedEquityUsd);Assert.Equal(0,result.Diagnostics.PositionCount);return;}
        Assert.Equal(1,result.Diagnostics.PositionCount);Assert.Equal(1,result.Diagnostics.OpenOrderCount);Assert.NotNull(result.Inventory);
        Assert.Equal(mode=="cross",result.Diagnostics.CrossCollateralObserved);Assert.Equal(mode=="portfolio",result.Diagnostics.PortfolioMarginObserved);
        Assert.Equal(mode!="unsupported-asset",result.Diagnostics.UsdEstimateComplete);
        Assert.Equal(mode=="unsupported-asset"?(decimal?)null:9m,result.Diagnostics.EstimatedEquityUsd);
        var privateJson=JsonSerializer.Serialize(result);Assert.DoesNotContain("synthetic-secret",privateJson);Assert.DoesNotContain("synthetic-token",privateJson);
    }
    [Theory]
    [InlineData("purpose")][InlineData("environment")][InlineData("account-type")][InlineData("id")][InlineData("secret")]
    public async Task InvalidInputFailsBeforeAnyAuthentication(string variant)
    {
        var input=Input();if(variant=="purpose")input.Purpose="TRADE";if(variant=="environment")input.Environment="Testnet";
        if(variant=="account-type")input.AccountType="parent-selected";if(variant=="id")input.ExpectedAccountId=0;if(variant=="secret")input.ClientSecret="bad\nsecret";
        using var venue=new Venue("");using var http=new HttpClient(venue);
        await Assert.ThrowsAsync<ArgumentException>(()=>AccountInspection.CollectAsync(input,http,default));Assert.Equal(0,venue.Calls);
    }
    [Theory]
    [InlineData("price-id")][InlineData("price-error")][InlineData("price-negative")][InlineData("price-redirect")][InlineData("price-overflow")]
    public async Task UntrustedPublicValuationCannotProduceAPrivateReviewArtifact(string mode)
    {
        using var venue=new Venue(mode);using var http=new HttpClient(venue);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>AccountInspection.CollectAsync(Input(),http,default));
        Assert.True(venue.Calls>=5);
    }
    private sealed class Venue(string mode):HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;Assert.Equal("https",request.RequestUri!.Scheme);Assert.Equal("www.deribit.com",request.RequestUri.Host);Assert.Empty(request.RequestUri.Query);Assert.Equal(HttpMethod.Post,request.Method);
            using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var r=json.RootElement;var p=r.GetProperty("params");object result;var method=r.GetProperty("method").GetString();
            Assert.Equal(method=="public/get_index_price"?"/api/v2":"/api/v2/"+method,request.RequestUri.AbsolutePath);
            if(method=="public/auth")
            {Assert.Null(request.Headers.Authorization);var scope=p.GetProperty("scope").GetString()!;Assert.DoesNotContain("read_write",scope);result=new{access_token="synthetic-token",expires_in=300,scope=mode=="write-grant"?scope.Replace("trade:read","trade:read_write"):scope};}
            else if(method=="public/get_index_price")
            {Assert.Null(request.Headers.Authorization);Assert.Equal("eth_usd",p.GetProperty("index_name").GetString());result=new{index_price=mode=="price-negative"?-1m:3000m};}
            else
            {
                Assert.Equal("synthetic-token",request.Headers.Authorization?.Parameter);
                result=method switch{
                    "private/get_account_summaries"=>new{id=123,type=mode=="wrong-type"?"main":"subaccount",summaries=new[]{
                        new{currency="ETH",balance=.003m,equity=.003m,cross_collateral_enabled=mode=="cross",portfolio_margining_enabled=mode=="portfolio"},
                        new{currency="USDC",balance=mode=="unsupported-asset"?1m:0m,equity=mode=="unsupported-asset"?1m:0m,cross_collateral_enabled=false,portfolio_margining_enabled=false}}},
                    "private/get_account_summary"=>new{id=123,type="subaccount",currency="ETH",margin_model=mode=="cross"?"cross_pm":"segregated_sm",
                        cross_collateral_enabled=mode=="cross",portfolio_margining_enabled=mode=="portfolio",balance=.003m,equity=.003m,available_funds=.002m,total_equity_usd=mode=="cross"?9m:(decimal?)null},
                    "private/get_positions"=>new[]{new{user_id=mode=="wrong-owner"?999:123,instrument_name="ETH-PERPETUAL",kind="future",direction="sell",size=-1,size_currency=-.001m}},
                    "private/get_open_orders"=>new[]{new{instrument_name="ETH-PERPETUAL",order_id="synthetic-original",order_state="open",order_type="limit"}},
                    _=>throw new InvalidOperationException("Any economic endpoint is forbidden in this inspection.")};
            }
            var payload=JsonSerializer.Serialize(new{jsonrpc="2.0",id=method=="public/get_index_price"&&mode=="price-id"?999:r.GetProperty("id").GetInt64(),result});
            if(method=="public/get_index_price"&&mode=="price-error")payload=JsonSerializer.Serialize(new{jsonrpc="2.0",id=r.GetProperty("id").GetInt64(),error=new{code=1}});
            if(method=="public/get_index_price"&&mode=="price-overflow")payload=new string('x',40000);
            return new(HttpStatusCode.OK){RequestMessage=method=="public/get_index_price"&&mode=="price-redirect"?new HttpRequestMessage(HttpMethod.Post,"https://untrusted.invalid/"):request,Content=new StringContent(payload)};
        }
    }
}
