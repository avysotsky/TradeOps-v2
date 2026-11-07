using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using QuickFix;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit.Fix;
using Xunit;

namespace TradeOps.UnitTests;
public sealed class DeribitFixConnectionProbeTests
{
    [Fact]
    public async Task ForeignCaptureScopeIsRejectedBeforeNativeAuthenticationOrConnection()
    {
        await using var capture=await OmsPrivateAccountRevisionTests.Setup.Open();var original=DeribitFixGatewayPostgresTests.Binding();
        var scope=new TradeOps.Domain.Oms.AccountKey(original.Scope.Tenant,original.Scope.Venue,original.Scope.Environment,new("foreign"));
        var binding=new TradeOps.Connectors.Deribit.DeribitPrivateCaptureBinding(scope,original.Options,original.Plan);
        var key=new OmsFixSessionKeyV1(1,scope,"foreign-probe","DERIBITSERVER");using var store=new DeribitQuickFixSessionTests.MemoryStore();
        using var pin=new PinHttp("valid");using var http=new HttpClient(pin);
        await Assert.ThrowsAsync<ArgumentException>(()=>DeribitFixConnectionProbeV1.RunAsync(binding,new(key),store,new Journal(),new(write=>write()),capture.Coordinator,capture.Session,http,()=>{}));
        Assert.Equal(0,pin.Calls);
    }
    [Theory]
    [InlineData("valid")][InlineData("valid-subaccount")][InlineData("wrong-pin")][InlineData("revoked")][InlineData("bad-logon")]
    [InlineData("wrong-heartbeat")][InlineData("unsolicited-logout")][InlineData("logout-cursor")]
    public async Task ProbeRequiresPinOwnedAdminIoCorrelatedHeartbeatLogoutAndCommittedCursors(string mode)
    {
        await using var capture=await OmsPrivateAccountRevisionTests.Setup.Open();
        var key=OmsFixSessionStorePostgresTests.Key("probe-"+Guid.NewGuid().ToString("N"));var binding=DeribitFixGatewayPostgresTests.Binding();
        if(mode=="valid-subaccount")
        {
            var scope=new TradeOps.Domain.Oms.AccountKey(key.Scope.Tenant,key.Scope.Venue,key.Scope.Environment,key.Scope.Account,new(key.Scope.Account.Value));
            key=new(2,scope,key.SenderCompId,key.TargetCompId);
            binding=new(scope,binding.Options,TradeOps.Connectors.Deribit.DeribitPrivateCapturePlan.ForAccountRecovery(123,true,binding.Plan.Instruments.Values));
        }
        using var memory=new DeribitQuickFixSessionTests.MemoryStore();
        using var pin=new PinHttp(mode);using var http=new HttpClient(pin);var wire=new PeerWire(key,mode,memory);var opened=0;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result=await DeribitFixConnectionProbeV1.RunCoreAsync(binding,new(key),mode=="logout-cursor"?new BrokenCursor(memory):memory,new Journal(),
            new OmsPhysicalSessionAuthority(write=>mode=="revoked"?throw new InvalidOperationException():write()),()=>capture.Coordinator.BeginExternalIngress(capture.Session),http,()=>{},
            _=>{opened++;return Task.FromResult<IDeribitFixWireV1>(wire);},deadline.Token);
        Assert.Equal(mode.StartsWith("valid",StringComparison.Ordinal)?"FIX_NONTRADING_CONNECTION_PASS":"FIX_NONTRADING_CONNECTION_REFUSED",result.Status);
        Assert.False(result.TradingReady||result.TradingStarted);Assert.Equal(0,result.BusinessMessagesSent);Assert.Equal(0,result.BusinessReportsReceived);
        Assert.All(wire.Types,t=>Assert.Contains(t,new[]{"A","0","1","2","4","5"}));
        if(mode=="wrong-pin"){Assert.Equal(0,opened);Assert.Empty(wire.Types);}
        if(mode.StartsWith("valid",StringComparison.Ordinal)){Assert.True(result.NativeBindingVerified&&result.LogonAccepted&&result.HeartbeatVerified&&result.LogoutAccepted);Assert.Equal(new[]{"A","1","5"},wire.Types);}
        var output=JsonSerializer.Serialize(result);Assert.DoesNotContain("synthetic-secret",output);Assert.DoesNotContain("synthetic-client",output);
    }
    [Theory]
    [InlineData("D")][InlineData("G")][InlineData("F")][InlineData("q")][InlineData("j")][InlineData("3")]
    public async Task LastMileWireBlocksEveryBusinessTypeEvenWhenGivenACompleteFrame(string type)
    {
        var key=OmsFixSessionStorePostgresTests.Key("blocked-"+Guid.NewGuid().ToString("N"));using var store=new DeribitQuickFixSessionTests.MemoryStore();
        var peer=new PeerWire(key,"",store);await using var gate=new DeribitFixAdminOnlyWireV1(peer,32768);
        var message=new Message();message.Header.SetField(new QuickFix.Fields.BeginString("FIX.4.4"));message.Header.SetField(new QuickFix.Fields.MsgType(type));
        message.Header.SetField(new QuickFix.Fields.SenderCompID(key.SenderCompId));message.Header.SetField(new QuickFix.Fields.TargetCompID(key.TargetCompId));
        message.Header.SetField(new QuickFix.Fields.MsgSeqNum(1));message.Header.SetField(new QuickFix.Fields.SendingTime(DateTime.UtcNow));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>gate.WriteAsync(Encoding.ASCII.GetBytes(message.ConstructString()),default));
        Assert.Empty(peer.Types);Assert.Equal(1,gate.BusinessBlocked);
    }
    internal sealed class Journal:IDeribitFixNativeReportJournal
    {public DeribitFixReportWriteResult Record(DeribitFixReportEnvelopeV1 report)=>throw new InvalidOperationException("No business report expected.");}
    internal sealed class PinHttp(string mode):HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;
            using var doc=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var root=doc.RootElement;object value;
            Assert.Equal("test.deribit.com",request.RequestUri!.Host);
            if(root.GetProperty("method").GetString()=="public/auth")
            {var scope=root.GetProperty("params").GetProperty("scope").GetString();Assert.DoesNotContain("read_write",scope);value=new{access_token="synthetic-token",scope,expires_in=300};}
            else value=new{id=mode=="wrong-pin"?999:123,type=mode=="valid-subaccount"?"subaccount":"main",currency="ETH",margin_model="cross_pm",cross_collateral_enabled=true,portfolio_margining_enabled=true,balance=.004m,equity=.004m,available_funds=.003m,total_equity_usd=8.9m};
            return new(HttpStatusCode.OK){RequestMessage=request,Content=new StringContent(JsonSerializer.Serialize(new{jsonrpc="2.0",id=root.GetProperty("id").GetInt64(),result=value}))};
        }
    }
    internal sealed class PeerWire(OmsFixSessionKeyV1 key,string mode,IOmsFixSessionStoreV1 store):IDeribitFixWireV1
    {
        internal readonly List<string> Types=[];private readonly Channel<byte[]> input=Channel.CreateUnbounded<byte[]>();private ulong sequence;
        public Task WriteAsync(ReadOnlyMemory<byte> bytes,CancellationToken ct)
        {
            var m=DeribitQuickFixSessionTests.Parse(Encoding.ASCII.GetString(bytes.Span));var type=m.Header.GetString(35);Types.Add(type);
            Assert.True(store.NextSender>m.Header.GetULong(34));
            if(type=="A")Send("A",(98,"0"),(108,mode=="bad-logon"?"10":"30"),(141,"N"),(9001,"N"));
            if(type=="1")
            {if(mode=="unsolicited-logout")Send("5");else Send("0",(112,mode=="wrong-heartbeat"?"wrong":m.GetString(112)));}
            if(type=="5")Send("5");return Task.CompletedTask;
        }
        private void Send(string type,params(int Tag,string Value)[] fields)
        {
            var m=new Message();m.Header.SetField(new QuickFix.Fields.BeginString("FIX.4.4"));m.Header.SetField(new QuickFix.Fields.MsgType(type));
            m.Header.SetField(new QuickFix.Fields.SenderCompID(key.TargetCompId));m.Header.SetField(new QuickFix.Fields.TargetCompID(key.SenderCompId));
            m.Header.SetField(new QuickFix.Fields.MsgSeqNum(++sequence));m.Header.SetField(new QuickFix.Fields.SendingTime(DateTime.UtcNow));
            foreach(var(tag,value)in fields)m.SetField(new QuickFix.Fields.StringField(tag,value));input.Writer.TryWrite(Encoding.ASCII.GetBytes(m.ConstructString()));
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct)
        {var bytes=await input.Reader.ReadAsync(ct);bytes.CopyTo(buffer);return bytes.Length;}
        public void Abort()=>input.Writer.TryComplete();
        public ValueTask DisposeAsync(){Abort();return ValueTask.CompletedTask;}
    }
    private sealed class BrokenCursor(DeribitQuickFixSessionTests.MemoryStore inner):IOmsFixSessionStoreV1
    {
        public DateTime CreationTimeUtc=>inner.CreationTimeUtc;public ulong NextSender=>inner.NextSender;public ulong NextTarget=>inner.NextTarget;
        public void SaveAndAdvance(ulong n,string type,string? text)=>inner.SaveAndAdvance(n,type,text);
        public void AdvanceSender(ulong n)=>inner.AdvanceSender(n);
        public void AdvanceTarget(ulong n){if(n>=4)throw new InvalidOperationException("Synthetic cursor commit failure.");inner.AdvanceTarget(n);}
        public IReadOnlyList<OmsFixStoredMessageV1> Read(ulong from,ulong through)=>inner.Read(from,through);
        public long ReserveLogonTimestamp(long now)=>inner.ReserveLogonTimestamp(now);public void Refresh(){}public void Dispose(){}
    }
}
