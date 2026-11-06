using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using QuickFix;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Connectors.Deribit.Fix;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;
using Live=TradeOps.UnitTests.DeribitRecoveryPublicationPostgresTests.Live;

namespace TradeOps.UnitTests;

public sealed class DeribitFixGatewayPostgresTests
{
    internal sealed class Http : HttpMessageHandler
    {
        internal long Account=123;internal decimal Contract=1;internal int PrivateCalls;internal Action? OnMetadata;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal("test.deribit.com",request.RequestUri!.Host);Assert.Equal("https",request.RequestUri.Scheme);
            using var d=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));var r=d.RootElement;
            var p=r.GetProperty("params");object result;
            switch(r.GetProperty("method").GetString())
            {
                case "public/auth":result=new{access_token="synthetic-token",expires_in=300,scope=p.GetProperty("scope").GetString()};break;
                case "private/get_account_summary":PrivateCalls++;Assert.Equal("Bearer",request.Headers.Authorization!.Scheme);
                    result=new{id=Account,type="main",currency="ETH",cross_collateral_enabled=false,portfolio_margining_enabled=false};break;
                case "public/get_instrument":OnMetadata?.Invoke();result=new{instrument_name=p.GetProperty("instrument_name").GetString(),kind="future",instrument_type="reversed",settlement_currency="ETH",base_currency="ETH",quote_currency="USD",
                    contract_size=Contract,min_trade_amount=1,is_active=true,expiration_timestamp=DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeMilliseconds()};break;
                default:throw new InvalidOperationException("Unexpected HTTP operation.");
            }
            return new(HttpStatusCode.OK){RequestMessage=request,Content=new StringContent(JsonSerializer.Serialize(new{jsonrpc="2.0",id=r.GetProperty("id").GetInt64(),result}))};
        }
    }
    internal sealed class Wire(OmsFixSessionKeyV1 key,ulong firstSequence=1):IDeribitFixWireV1
    {
        private readonly Channel<byte[]> incoming=Channel.CreateBounded<byte[]>(32);
        private ulong sequence=firstSequence;private Message? placement;
        internal int Placements,Cancels;internal bool IgnorePlace,IgnoreCancel,Partial;internal Func<Message,Task>? BeforeWrite;
        internal string? LastFrame;
        public async Task WriteAsync(ReadOnlyMemory<byte> bytes,CancellationToken ct)
        {
            var m=DeribitQuickFixSessionTests.Parse(Encoding.ASCII.GetString(bytes.Span));if(BeforeWrite is not null)await BeforeWrite(m);
            var type=m.Header.GetString(35);var reply=new Message();reply.Header.SetField(new QuickFix.Fields.BeginString("FIX.4.4"));
            reply.Header.SetField(new QuickFix.Fields.SenderCompID("DERIBITSERVER"));reply.Header.SetField(new QuickFix.Fields.TargetCompID(key.SenderCompId));
            reply.Header.SetField(new QuickFix.Fields.MsgSeqNum(sequence++));reply.Header.SetField(new QuickFix.Fields.SendingTime(DateTime.UtcNow));
            if(type=="A")
            {reply.Header.SetField(new QuickFix.Fields.MsgType("A"));reply.SetField(new QuickFix.Fields.EncryptMethod(0));reply.SetField(new QuickFix.Fields.HeartBtInt(30));}
            else if(type is "D" or "F")
            {
                if(type=="D"){Placements++;placement=m;if(IgnorePlace)return;}else{Cancels++;if(IgnoreCancel)return;}
                var p=placement!;reply.Header.SetField(new QuickFix.Fields.MsgType("8"));
                var now=DateTime.UtcNow.ToString("yyyyMMdd-HH:mm:ss.fff",System.Globalization.CultureInfo.InvariantCulture);
                foreach(var(tag,value)in new(int,string)[]{(37,"native-order"),(11,"native-server-client"),(41,p.GetString(11)),(527,"change-"+sequence),(39,type=="F"?"4":Partial?"1":"0"),
                    (54,p.GetString(54)),(60,now),(151,type=="F"?"0":Partial?"0.5":p.GetString(38)),(14,Partial?"0.5":"0"),(38,p.GetString(38)),(152,p.GetString(38)),
                    (40,"2"),(44,p.GetString(44)),(103,"0"),(55,p.GetString(55)),(231,"1"),(854,"1"),(59,"1"),(18,p.IsSetField(18)?p.GetString(18):""),(100010,p.GetString(100010))})
                    if(value!="")reply.SetField(new QuickFix.Fields.StringField(tag,value));
            }
            else return;
            LastFrame=reply.ConstructString();await incoming.Writer.WriteAsync(Encoding.ASCII.GetBytes(LastFrame),ct);
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct)
        {var bytes=await incoming.Reader.ReadAsync(ct);bytes.CopyTo(buffer);return bytes.Length;}
        public void Abort()=>incoming.Writer.TryComplete();
        public ValueTask DisposeAsync(){Abort();return ValueTask.CompletedTask;}
    }
    private sealed class Journal(IDeribitFixNativeReportJournal inner,bool loseAck):IDeribitFixNativeReportJournal
    {
        public DeribitFixReportWriteResult Record(DeribitFixReportEnvelopeV1 r)
        {var result=inner.Record(r);if(loseAck)throw new IOException("Synthetic loss after native COMMIT.");return result;}
    }
    private sealed class Policies:IOmsRiskPolicyReader
    {
        public Task<OmsRiskPolicyDocumentV1?> ReadAsync(TradeOps.Domain.Oms.AccountKey scope,CancellationToken ct=default)
        {var p=DeribitRiskInputsTests.Policy();return Task.FromResult<OmsRiskPolicyDocumentV1?>(p with{Limits=p.Limits with{Scope=scope},ValidFromUtc=DateTimeOffset.UtcNow.AddMinutes(-1),ValidThroughUtc=DateTimeOffset.UtcNow.AddHours(1)});}
    }
    internal static DeribitPrivateCaptureBinding Binding()=>new(Scope(),new DeribitOptions{ClientId="synthetic-client",ClientSecret="synthetic-secret",AccountCurrency="ETH"},new(123,false,
        [new("ETH-PERPETUAL","future","ETH",OmsPrivateAmountUnit.Usd,"USD")]));

    [Theory]
    [InlineData("normal")][InlineData("lost-ack")][InlineData("unknown")][InlineData("partial")][InlineData("cancel-unknown")][InlineData("restricted")][InlineData("ingress-race")][InlineData("cancel-under-kill")]
    public async Task ActualEngineUsesDurableAdmissionNativeEvidenceRecoveryAndAddressedCancelWithoutWsReceiptFabrication(string mode)
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new OmsExecutionGatewayPostgresTests.Root();await root.Bind(db.Source);
        await using var live=await Live.Open(db);var recovery=new OmsExecutionGatewayPostgresTests.Recovery();
        var key=OmsFixSessionStorePostgresTests.Key("gateway-"+Guid.NewGuid().ToString("N"));await OmsFixSessionStorePostgresTests.Seed(db.Source,key);
        await using var roles=mode=="restricted"?await Roles.Open(db):null;
        var execution=roles?.Execution??db.Source;var protocolSource=roles?.Protocol??db.Source;
        using var protocol=new OmsPostgresFixSessionStoreV1(protocolSource,key);using var handler=new Http();using var http=new HttpClient(handler);
        var wire=new Wire(key){IgnorePlace=mode=="unknown",IgnoreCancel=mode=="cancel-unknown",Partial=mode=="partial"};
        wire.BeforeWrite=async m=>
        {if(m.Header.GetString(35) is "D" or "F"){Assert.True(await OmsExecutionGatewayPostgresTests.Count(db,"oms_deribit_fix_command_bindings_v1")>=1);Assert.Equal(1,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_reservations_v1"));}};
        DeribitOmsFixTransportV1? transport=null;
        await using var gateway=await OmsPostgresExecutionGateway.OpenAsync(Scope(),execution,recovery,new OmsRiskAdmissionPostgresTests.Inputs(),root.PathName,
            async(authority,ct)=>transport=await DeribitOmsFixTransportV1.OpenCoreAsync(Binding(),new(key),protocol,
                new Journal(new OmsPostgresDeribitFixReportJournalV1(protocolSource,key),mode=="lost-ack"),authority,
                ()=>live.Coordinator.BeginExternalIngress(live.Session),http,(c,t)=>OmsPostgresFixParentReaderV1.ReadAsync(execution,c,t),
                recovery.RequestRecovery,_=>Task.FromResult<IDeribitFixWireV1>(wire),ct),cancelPolicies:new Policies());
        async Task Refresh()
        {
            var r=await live.Recover(true);Assert.True(r.Coherence.EvidenceSufficient,string.Join(",",r.Coherence.Problems));
            recovery.Current=r.ExecutionBaseline;Assert.NotNull(recovery.Current);
        }
        await Refresh();OmsExternalEconomicIngress? ingress=null;
        if(mode=="ingress-race")handler.OnMetadata=()=>ingress=live.Coordinator.BeginExternalIngress(live.Session);
        var command=OmsExecutionGatewayPostgresTests.Command();var result=await gateway.SubmitAsync(command);
        if(mode=="ingress-race")
        {
            Assert.Equal(OmsExecutionResultKind.Unknown,result.Kind);Assert.Equal(0,wire.Placements);Assert.NotNull(ingress);ingress.Dispose();
            Assert.Equal(1,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_reservations_v1"));return;
        }
        Assert.Equal(mode is "lost-ack" or "unknown"?OmsExecutionResultKind.Unknown:OmsExecutionResultKind.Acknowledged,result.Kind);
        Assert.Equal(1,wire.Placements);Assert.Equal(0,await OmsExecutionGatewayPostgresTests.Count(db,"oms_deribit_command_receipts_v1"));
        var ledger=await new OmsPostgresRecoveryLedgerReader(db.Source,true).ReadAsync(Scope());Assert.True(ledger.IsUsable,string.Join(",",ledger.Problems));
        Assert.Single(ledger.FixBindings!);Assert.Empty(ledger.Private.Observations);
        if(mode=="unknown")
        {
            Assert.Empty(DeribitNativeCommandEvidenceV1.Read(ledger));var blocked=await live.Recover(true);Assert.Null(blocked.ExecutionBaseline);
            Assert.Equal(1,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_reservations_v1"));return;
        }
        var view=Assert.Single(DeribitNativeCommandEvidenceV1.Read(ledger));view.Validate();live.NativeReport=view.Order;
        if(mode=="partial")
        {
            Assert.Equal(.5m,view.Order.FilledNativeAmount);var blocked=await live.Recover(true);Assert.Null(blocked.ExecutionBaseline);
            Assert.Empty(blocked.Document.Candidate?.Executions??[]);Assert.Equal(0,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_settlements_v1"));return;
        }
        if(mode=="lost-ack")
        {
            // A callback fault poisons the old ingress baseline. Restart creates fresh WS
            // capture/recovery authority; durable FIX evidence survives without resending.
            await using var restarted=await Live.Open(db);restarted.NativeReport=view.Order;
            var recovered=await restarted.Recover(true);Assert.True(recovered.Coherence.EvidenceSufficient,string.Join(",",recovered.Coherence.Problems));
            Assert.Equal(OmsRecoveryDispatchPhase.Acknowledged,Assert.Single((await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(Scope())).Commands).Phase);
            Assert.Equal(1,wire.Placements);
            await gateway.DisposeAsync();
            using var nextProtocol=new OmsPostgresFixSessionStoreV1(db.Source,key);var nextWire=new Wire(key,nextProtocol.NextTarget);
            var nextRecovery=new OmsExecutionGatewayPostgresTests.Recovery();
            await using var nextGateway=await OmsPostgresExecutionGateway.OpenAsync(Scope(),db.Source,nextRecovery,new OmsRiskAdmissionPostgresTests.Inputs(),root.PathName,
                async(authority,ct)=>await DeribitOmsFixTransportV1.OpenCoreAsync(Binding(),new(key),nextProtocol,
                    new OmsPostgresDeribitFixReportJournalV1(db.Source,key),authority,()=>restarted.Coordinator.BeginExternalIngress(restarted.Session),http,
                    (c,t)=>OmsPostgresFixParentReaderV1.ReadAsync(db.Source,c,t),nextRecovery.RequestRecovery,_=>Task.FromResult<IDeribitFixWireV1>(nextWire),ct));
            var fresh=await restarted.Recover(true);Assert.NotNull(fresh.ExecutionBaseline);nextRecovery.Current=fresh.ExecutionBaseline;
            Assert.Equal(OmsExecutionResultKind.Denied,(await nextGateway.SubmitAsync(command)).Kind);Assert.Equal(0,nextWire.Placements);return;
        }
        await Refresh();
        if(mode=="cancel-under-kill")
        {
            await using(var q=db.Source.CreateCommand("INSERT INTO oms_risk_control_permissions_v1 VALUES(3,'tenant','DERIBIT',3,'account','',1,current_user,true,false)"))await q.ExecuteNonQueryAsync();
            await new OmsPostgresRiskControls(db.Source).SetAsync(new(OmsRiskControlScopeV1.ForAccount(Scope()),OmsRiskControlKind.KillSwitch,Guid.NewGuid(),0,true,"Operator test stop"));
        }
        var cancel=DeribitCommandReceiptFixtures.Cancel(command,view.Order.ExchangeOrderId);
        var cancelled=await gateway.SubmitAsync(cancel);
        Assert.Equal(mode=="cancel-unknown"?OmsExecutionResultKind.Unknown:OmsExecutionResultKind.Acknowledged,cancelled.Kind);Assert.Equal(1,wire.Cancels);
        if(mode=="cancel-unknown")
        {Assert.Null((await live.Recover(true)).ExecutionBaseline);Assert.Equal(0,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_settlements_v1"));return;}
        ledger=await new OmsPostgresRecoveryLedgerReader(db.Source,true).ReadAsync(Scope());
        var views=DeribitNativeCommandEvidenceV1.Read(ledger);Assert.Equal(2,views.Count);live.NativeReport=views.Single(x=>x.Command.Payload.Kind==OmsJournalCommandKind.Cancel).Order;
        await Refresh();Assert.True(OmsRiskSettlementV1.CanRetire((await live.Store.ReadPublishedAsync(Scope()))!.State,view));
        if(mode=="cancel-under-kill")
        {Assert.Equal(OmsExecutionResultKind.Denied,(await gateway.SubmitAsync(OmsExecutionGatewayPostgresTests.Command())).Kind);Assert.Equal(1,wire.Placements);return;}
        Assert.Equal(OmsRiskAdmissionResult.Reserved,await new OmsPostgresRiskAdmission(db.Source,new OmsRiskAdmissionPostgresTests.Inputs()).AdmitAsync(
            OmsExecutionGatewayPostgresTests.Command(),recovery.Current!.Publication,recovery.Current.Qualified));
        Assert.Equal(1,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_settlements_v1"));
    }

    private sealed class Roles(OmsPostgresTestDatabase db):IAsyncDisposable
    {
        private readonly string execution="to_fixexec_"+Guid.NewGuid().ToString("N"),protocol="to_fixproto_"+Guid.NewGuid().ToString("N");
        internal NpgsqlDataSource Execution=null!,Protocol=null!;
        internal static async Task<Roles> Open(OmsPostgresTestDatabase db)
        {
            var r=new Roles(db);
            foreach(var name in new[]{r.execution,r.protocol})
            {await using var q=db.Source.CreateCommand($"CREATE ROLE \"{name}\" NOLOGIN; COMMENT ON ROLE \"{name}\" IS 'tradeops-tests:role:v1:{db.Name}'; GRANT USAGE ON SCHEMA public TO \"{name}\"");await q.ExecuteNonQueryAsync();}
            await using(var q=db.Source.CreateCommand($"""
GRANT SELECT ON ALL TABLES IN SCHEMA public TO "{r.execution}";
GRANT INSERT ON oms_aggregates,oms_intents,oms_commands,oms_attempts,oms_outbox,oms_integrity_incidents,oms_deribit_command_receipts_v1,
 oms_risk_reservations_v1,oms_risk_settlements_v1,oms_risk_admission_controls_v1,oms_risk_dispatches_v1,oms_risk_admission_audit_v1,
 oms_risk_cancel_admissions_v1,oms_execution_dispatches_v2,oms_deribit_fix_command_bindings_v1 TO "{r.execution}";
GRANT UPDATE ON oms_aggregates,oms_intents,oms_commands,oms_attempts,oms_outbox TO "{r.execution}";
GRANT USAGE ON SEQUENCE oms_risk_reservations_v1_reservation_id_seq TO "{r.execution}";
GRANT EXECUTE ON FUNCTION public.oms_lock_evidence_version_v1(text,text,smallint,text,text),
 public.oms_lock_risk_recovery_head_v1(text,text,smallint,text,text),public.oms_risk_control_lock_key_v1(smallint,text,text,smallint,text,text),
 public.oms_set_risk_control_v1(smallint,text,text,smallint,text,text,smallint,uuid,bigint,boolean,text) TO "{r.execution}";
GRANT SELECT ON oms_fix_sessions_v1,oms_fix_outbound_messages_v1,oms_deribit_fix_reports_v1,
 oms_deribit_fix_native_events_v1,oms_deribit_fix_report_incidents_v1,oms_evidence_versions_v1 TO "{r.protocol}";
GRANT INSERT ON oms_fix_outbound_messages_v1,oms_deribit_fix_reports_v1,oms_deribit_fix_native_events_v1,oms_deribit_fix_report_incidents_v1 TO "{r.protocol}";
GRANT UPDATE(next_sender,next_target,last_logon_ms) ON oms_fix_sessions_v1 TO "{r.protocol}";
GRANT INSERT(tenant_id,venue_id,environment,account_id,subaccount_id) ON oms_evidence_versions_v1 TO "{r.protocol}","{r.execution}";
"""))await q.ExecuteNonQueryAsync();
            r.Execution=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(db.ConnectionString){Pooling=true,MaxPoolSize=8,Options="-c role="+r.execution}.ConnectionString);
            r.Protocol=NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(db.ConnectionString){Pooling=true,MaxPoolSize=8,Options="-c role="+r.protocol}.ConnectionString);
            await OmsPostgresExecutionRuntimeGuard.ValidateAsync(r.Execution);await OmsPostgresFixRuntimeGuardV1.ValidateAsync(r.Protocol);return r;
        }
        public async ValueTask DisposeAsync()
        {
            if(Execution is not null)await Execution.DisposeAsync();if(Protocol is not null)await Protocol.DisposeAsync();
            foreach(var name in new[]{execution,protocol}){await using var q=db.Source.CreateCommand($"DROP OWNED BY \"{name}\"; DROP ROLE \"{name}\"");await q.ExecuteNonQueryAsync();}
        }
    }
}
