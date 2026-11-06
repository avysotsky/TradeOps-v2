using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;
using Live=TradeOps.UnitTests.DeribitRecoveryPublicationPostgresTests.Live;
using FrozenClock=TradeOps.UnitTests.OmsRiskAdmissionPostgresTests.FrozenClock;
using Inputs=TradeOps.UnitTests.OmsRiskAdmissionPostgresTests.Inputs;

namespace TradeOps.UnitTests;
public sealed class DeribitOmsTransportPostgresTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualConnectorWritesNativeAmountOnlyAfterGatewayPermitAndNeverRetriesLostAck(bool lostAck)
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new OmsExecutionGatewayPostgresTests.Root();await root.Bind(db.Source);
        var clock=new FrozenClock();await using var live=await Live.Open(db,clock);var recovery=await OmsExecutionGatewayPostgresTests.Prepare(live);
        var socket=new Socket{LostAck=lostAck};var binding=Binding();
        await using var gateway=await TradeOps.Infrastructure.Persistence.Oms.OmsPostgresExecutionGateway.OpenAsync(Scope(),db.Source,recovery,new Inputs(clock),root.PathName,
            async ct=>await DeribitOmsWebSocketTransport.OpenAsync(binding,socket,ct),clock);
        var c=OmsExecutionGatewayPostgresTests.Command();var result=await gateway.SubmitAsync(c);
        Assert.Equal(lostAck?OmsExecutionResultKind.Unknown:OmsExecutionResultKind.Acknowledged,result.Kind);
        Assert.Equal(1,socket.Trades);Assert.Equal(1,socket.NativeAmount);Assert.False(socket.UsedContracts);
        Assert.Equal(lostAck?0:1,await OmsExecutionGatewayPostgresTests.Count(db,"oms_deribit_command_receipts_v1"));
    }
    [Fact]
    public async Task WrongNativePinCannotEstablishOwnedTransportOrWriteAnOrder()
    {
        var socket=new Socket{WrongPin=true};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>DeribitOmsWebSocketTransport.OpenAsync(Binding(),socket,CancellationToken.None));
        Assert.Equal(0,socket.Trades);Assert.True(socket.Aborted);
    }
    [Fact]
    public async Task DirectConnectorWithoutNonserializablePermitCannotWrite()
    {
        var socket=new Socket();await using var transport=await DeribitOmsWebSocketTransport.OpenAsync(Binding(),socket,CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentNullException>(()=>transport.SendAsync(OmsExecutionGatewayPostgresTests.Command(),null!,CancellationToken.None));Assert.Equal(0,socket.Trades);
        Assert.Empty(typeof(OmsPhysicalWritePermit).GetConstructors());
    }
    private static DeribitPrivateCaptureBinding Binding()=>new(Scope(),Options(),new(123,false,[new("ETH-PERPETUAL","future","ETH",OmsPrivateAmountUnit.Usd,"USD")]));
    [Fact]
    public async Task FullB4PolicyNativeRiskPendingAdmissionAndSocketPipelineWorksWithoutGenericRiskDefaults()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new OmsExecutionGatewayPostgresTests.Root();await root.Bind(db.Source);
        var clock=new FrozenClock();var policy=DeribitRiskInputsTests.Policy() with{Limits=DeribitRiskInputsTests.Policy().Limits with{Scope=Scope()},
            ValidFromUtc=clock.GetUtcNow().AddMinutes(-1),ValidThroughUtc=clock.GetUtcNow().AddHours(1)};
        await OmsRiskPolicyAndRolePostgresTests.InsertPolicy(db,policy,1);
        await using var live=await Live.Open(db,clock);live.Cash=1;live.Equity=1;var recovery=await OmsExecutionGatewayPostgresTests.Prepare(live);
        using var server=new DeribitRiskObservationReaderTests.Server("funded",clock.GetUtcNow);using var http=new HttpClient(server);
        var pending=new TradeOps.Infrastructure.Persistence.Oms.OmsPostgresPendingRiskReader(db.Source);
        var policies=new TradeOps.Infrastructure.Persistence.Oms.OmsPostgresRiskPolicyReader(db.Source);
        var risk=new DeribitRiskInputsReader(policies,new DeribitRiskObservationReader(Binding(),http,clock,pending),clock,pending);
        var socket=new Socket();
        await using var gateway=await TradeOps.Infrastructure.Persistence.Oms.OmsPostgresExecutionGateway.OpenAsync(Scope(),db.Source,recovery,risk,root.PathName,
            async ct=>await DeribitOmsWebSocketTransport.OpenAsync(Binding(),socket,ct),clock,cancelPolicies:policies);
        Assert.Equal(OmsExecutionResultKind.Acknowledged,(await gateway.SubmitAsync(OmsExecutionGatewayPostgresTests.Command())).Kind);
        Assert.Equal(1,socket.Trades);Assert.Equal(1,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_reservations_v1"));
        Assert.Contains("private/get_margins",server.Methods);Assert.Equal(1,server.Methods.Count(x=>x=="private/get_margins"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualNativeCancellationUsesOnlyTheVerifiedTargetAndRetainsUnknownRisk(bool lostAck)
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new OmsExecutionGatewayPostgresTests.Root();await root.Bind(db.Source);
        await using var live=await Live.Open(db);var(c,receipt,recovery)=await OmsAddressedCancelPostgresTests.Seed(db,live);
        var socket=new Socket{LostAck=lostAck,NativeLabel=c.Payload.TargetClientOrderId!};
        await using var gateway=await TradeOps.Infrastructure.Persistence.Oms.OmsPostgresExecutionGateway.OpenAsync(Scope(),db.Source,recovery,new Inputs(),root.PathName,
            async ct=>await DeribitOmsWebSocketTransport.OpenAsync(Binding(),socket,ct),cancelPolicies:new OmsAddressedCancelPostgresTests.Policies());
        var result=await gateway.SubmitAsync(DeribitCommandReceiptFixtures.Cancel(c,receipt.Order.ExchangeOrderId));
        Assert.Equal(lostAck?OmsExecutionResultKind.Unknown:OmsExecutionResultKind.Acknowledged,result.Kind);Assert.Equal(1,socket.Trades);
        Assert.Equal(1,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_reservations_v1"));Assert.Equal(0,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_settlements_v1"));
    }
    private sealed class Socket:IDeribitPrivateSocket
    {
        internal bool WrongPin,LostAck,Aborted,UsedContracts;internal int Trades;internal decimal NativeAmount;internal string NativeLabel="";private JsonElement last;
        public Task ConnectAsync(Uri uri,CancellationToken ct){Assert.Equal("test.deribit.com",uri.Host);return Task.CompletedTask;}
        public Task SendAsync(ReadOnlyMemory<byte> bytes,CancellationToken ct)
        {
            using var d=JsonDocument.Parse(bytes);last=d.RootElement.Clone();var method=last.GetProperty("method").GetString();
            if(method is "private/buy" or "private/sell") { Trades++;var p=last.GetProperty("params");NativeAmount=p.GetProperty("amount").GetDecimal();UsedContracts=p.TryGetProperty("contracts",out _);Assert.True(p.GetProperty("post_only").GetBoolean());Assert.True(p.GetProperty("reject_post_only").GetBoolean()); }
            if(method=="private/cancel"){Trades++;Assert.Single(last.GetProperty("params").EnumerateObject());Assert.Equal("native-1",last.GetProperty("params").GetProperty("order_id").GetString());}
            return Task.CompletedTask;
        }
        public Task<string> ReceiveAsync(int max,CancellationToken ct)
        {
            var method=last.GetProperty("method").GetString();var p=last.GetProperty("params");object result;
            if(method=="public/auth")result=new{scope=p.GetProperty("scope").GetString(),expires_in=300};
            else if(method=="private/get_account_summary")result=new{id=WrongPin?999:123,type="main",currency="BTC"};
            else
            {
                if(LostAck)throw new IOException("Lost scripted acknowledgement.");
                if(method=="private/cancel")result=new{instrument_name="ETH-PERPETUAL",order_id="native-1",direction="buy",amount=1,order_state="cancelled",filled_amount=0,last_update_timestamp=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),price=2000,order_type="limit",time_in_force="good_til_cancelled",post_only=true,reduce_only=false,label=NativeLabel};
                else result=new{order=new{instrument_name=p.GetProperty("instrument_name").GetString(),order_id="native-owned",direction=method=="private/buy"?"buy":"sell",amount=p.GetProperty("amount").GetDecimal(),
                    order_state="open",filled_amount=0,last_update_timestamp=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),price=p.GetProperty("price").GetDecimal(),order_type="limit",time_in_force="good_til_cancelled",post_only=true,reduce_only=false,label=p.GetProperty("label").GetString()}};
            }
            return Task.FromResult(JsonSerializer.Serialize(new{jsonrpc="2.0",id=last.GetProperty("id").GetInt64(),result}));
        }
        public void Abort()=>Aborted=true;public void Dispose()=>Aborted=true;
    }
}
