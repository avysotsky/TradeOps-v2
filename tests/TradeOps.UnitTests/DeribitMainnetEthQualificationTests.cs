using System.Net;
using System.Text;
using System.Text.Json;
using TradeOps.DeribitTestnetOrderQualification;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;

public sealed class DeribitMainnetEthQualificationTests
{
    [Theory]
    [InlineData(0,"Mainnet","MAINNET_SMALL_ETH_ONE_ORDER_V1")]
    [InlineData(123,"Testnet","MAINNET_SMALL_ETH_ONE_ORDER_V1")]
    [InlineData(123,"Mainnet","TESTNET_ONE_POSTONLY_ORDER_CANCEL")]
    public async Task MissingPinOrWrongPurpose_RefusedBeforeNetwork(long pin,string environment,string purpose)
    {
        var input=Input();input.ExpectedAccountId=pin;input.Environment=environment;input.Purpose=purpose;
        var venue=new Venue();using var http=new HttpClient(venue);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>TestnetOrderQualification.RunMainnetEthAsync(
            input,http,Path.GetTempPath(),CancellationToken.None));
        Assert.Equal(0,venue.Calls);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public void MainnetEth_RefusesMoreThanOneNativeUsd(decimal minimum)
    {
        Assert.Throws<InvalidOperationException>(()=>TestnetOrderQualification.ValidateRisk(
            JsonSerializer.SerializeToElement(Metadata(minimum)),JsonSerializer.SerializeToElement(Book()),DateTimeOffset.UtcNow,true));
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("lost-ack")]
    [InlineData("position-changed")]
    public async Task MainnetEth_OneUsdCommitAndTargetedCancel_PreserveExistingPosition(string scenario)
    {
        var socket=new Socket();var venue=new Venue {Socket=socket,Scenario=scenario};using var http=new HttpClient(venue);
        var directory=Path.Combine(Path.GetTempPath(),"tradeops-mainnet-scripted-"+Guid.NewGuid().ToString("N"));
        try
        {
            var result=await TestnetOrderQualification.RunMainnetEthAsync(Input(),http,directory,CancellationToken.None,
                settings=>Stream(()=>socket,settings),(_,ct)=>QualificationDatabase.OpenAsync(false,ct));
            Assert.Equal(1,venue.Placements);Assert.Equal(1,venue.Cancellations);
            Assert.Equal("www.deribit.com",socket.Endpoint!.Host);
            Assert.Equal("Mainnet",result.Environment);Assert.True(result.ExchangeOrderResolved);
            Assert.False(result.JournalRetained); // This injected store is a test clone, not the persistent live store.
            Assert.True(result.CancelEventCommitted);Assert.False(result.TradingReady);
            Assert.Equal(scenario!="position-changed",result.BaselinePositionPreserved);
            Assert.Equal(scenario=="normal"?"MAINNET_ETH_CAPTURE_PASS":"MAINNET_ETH_CAPTURE_FAILED",result.Status);
            Assert.DoesNotContain("synthetic-secret",JsonSerializer.Serialize(result));
            Assert.DoesNotContain("eth-order-scripted",JsonSerializer.Serialize(result));
        }
        finally
        {
            if(Directory.Exists(directory)) {File.Delete(Path.Combine(directory,"attempt.json"));File.Delete(Path.Combine(directory,"run.lock"));Directory.Delete(directory);}
        }
    }

    [Fact]
    public async Task PersistentMainnetStore_RefusesReusableTestServerBeforeMigration()
    {
        var old=Environment.GetEnvironmentVariable("TRADEOPS_MAINNET_QUALIFICATION_POSTGRES");
        try
        {
            Environment.SetEnvironmentVariable("TRADEOPS_MAINNET_QUALIFICATION_POSTGRES",Environment.GetEnvironmentVariable("TRADEOPS_B2_POSTGRES"));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>QualificationDatabase.OpenAsync(true,CancellationToken.None));
        }
        finally {Environment.SetEnvironmentVariable("TRADEOPS_MAINNET_QUALIFICATION_POSTGRES",old);}
    }

    [Fact]
    public async Task OriginalTestnetEntrypoint_StillRefusesMainnetInput()
    {
        var venue=new Venue();using var http=new HttpClient(venue);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>TestnetOrderQualification.RunAsync(Input(),http,Path.GetTempPath(),CancellationToken.None));
        Assert.Equal(0,venue.Calls);
    }

    private static TestnetOrderInput Input()=>new(){Purpose="MAINNET_SMALL_ETH_ONE_ORDER_V1",Environment="Mainnet",
        ApprovedOneOrderAndCancellation=true,ExpectedAccountId=123,AccountType="main",ClientId="synthetic-client",ClientSecret="synthetic-secret"};
    private static object Metadata(decimal amount=1)=>new{instrument_name="ETH-PERPETUAL",kind="future",instrument_type="reversed",
        base_currency="ETH",quote_currency="USD",settlement_currency="ETH",is_active=true,min_trade_amount=amount,tick_size=.05m,tick_size_steps=Array.Empty<object>()};
    private static object Book()=>new{instrument_name="ETH-PERPETUAL",state="open",best_bid_price=2500m,best_ask_price=2500.05m,
        min_price=2300m,max_price=2700m,timestamp=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()};
    private sealed class Venue:HttpMessageHandler
    {
        internal Socket? Socket;internal string Scenario="normal";internal int Calls,Placements,Cancellations;
        private int positionReads;private string label="";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal("www.deribit.com",request.RequestUri!.Host);Assert.Equal(HttpMethod.Post,request.Method);Calls++;
            using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root=json.RootElement;var p=root.GetProperty("params");object result;
            switch(root.GetProperty("method").GetString())
            {
                case "public/auth":result=new{access_token="synthetic-token",expires_in=300,scope=p.GetProperty("scope").GetString()};break;
                case "private/get_account_summary":Assert.Equal("ETH",p.GetProperty("currency").GetString());
                    result=new{id=123,type="main",total_equity_usd=8.9m,total_margin_balance_usd=8.9m,total_initial_margin_usd=.1m};break;
                case "private/get_open_orders_by_currency":result=Array.Empty<object>();break;
                case "private/get_positions":positionReads++;result=new[]{new{instrument_name="ETH-PERPETUAL",direction="sell",size=Scenario=="position-changed"&&positionReads>1?11m:10m}};break;
                case "public/get_instrument":result=Metadata();break;
                case "public/get_order_book":result=Book();break;
                case "public/get_time":result=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();break;
                case "private/buy":
                    Placements++;Assert.Equal(1,Placements);Assert.Equal(1,p.GetProperty("amount").GetDecimal());
                    Assert.Equal("ETH-PERPETUAL",p.GetProperty("instrument_name").GetString());
                    Assert.True(p.GetProperty("post_only").GetBoolean());Assert.True(p.GetProperty("reject_post_only").GetBoolean());
                    Assert.InRange(p.GetProperty("valid_until").GetInt64()-DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),1,5000);
                    label=p.GetProperty("label").GetString()!;Socket!.Emit(Notification("open"));
                    if(Scenario=="lost-ack")throw new HttpRequestException("Scripted lost ACK");result=new{order=Order("open")};break;
                case "private/cancel_by_label":Cancellations++;Assert.Equal("ETH",p.GetProperty("currency").GetString());Assert.Equal(label,p.GetProperty("label").GetString());
                    Socket!.Emit(Notification("cancelled"));result=1;break;
                case "private/get_order_state_by_label":result=new[]{Order("cancelled")};break;
                default:throw new InvalidOperationException("Unexpected mutation or query.");
            }
            return new(HttpStatusCode.OK){RequestMessage=request,Content=new StringContent(JsonSerializer.Serialize(new{jsonrpc="2.0",id=root.GetProperty("id").GetInt64(),result}),Encoding.UTF8,"application/json")};
        }
        private object Order(string state)=>new{instrument_name="ETH-PERPETUAL",order_id="eth-order-scripted",label,direction="buy",amount=1m,filled_amount=0m,
            price=2487.5m,order_state=state,last_update_timestamp=1793793600000L+(state=="cancelled"?1:0)};
        private string Notification(string state)=>JsonSerializer.Serialize(new{jsonrpc="2.0",method="subscription",@params=new{channel="user.orders.future.ETH.raw",data=Order(state)}});
    }
}
