using System.Text.Json;
using TradeOps.Application.Oms;

namespace TradeOps.Connectors.Deribit.Fix;

/// <summary>Supervised, pinned FIX transport. No reconnect, automatic resend of orders, schema
/// provisioning or independent owner. All engine writes use the gateway's physical authority.</summary>
public sealed class DeribitOmsFixTransportV1:IOmsFixAccountOrderTransport
{
    private readonly DeribitPrivateCaptureBinding binding;
    private readonly DeribitFixOptionsV1 options;
    private readonly HttpClient http;
    private readonly IDeribitFixNativeReportJournal journal;
    private readonly Func<OmsJournalCommandRequest,CancellationToken,Task<DeribitNativeCommandView?>> parent;
    private readonly Action recovery;
    private readonly CancellationTokenSource stop=new();
    private readonly object sync=new();
    private IDeribitFixWireV1 wire=null!;
    private DeribitQuickFixSessionV1 engine=null!;
    private Task reader=Task.CompletedTask,ticker=Task.CompletedTask;
    private DeribitFixCommandBindingV1? prepared;
    private DeribitFixInstrumentV1? instrument;
    private TaskCompletionSource<DeribitFixExecutionReportV1>? response;
    private OmsJournalCommandRequest? original;
    private readonly DateTimeOffset expires=DateTimeOffset.UtcNow.AddSeconds(240);
    private DeribitOmsFixTransportV1(DeribitPrivateCaptureBinding binding,DeribitFixOptionsV1 options,HttpClient http,
        IDeribitFixNativeReportJournal journal,Func<OmsJournalCommandRequest,CancellationToken,Task<DeribitNativeCommandView?>> parent,Action recovery)
    {this.binding=binding;this.options=options;this.http=http;this.journal=journal;this.parent=parent;this.recovery=recovery;}

    public static Task<DeribitOmsFixTransportV1> OpenAsync(DeribitPrivateCaptureBinding binding,DeribitFixOptionsV1 options,
        IOmsFixSessionStoreV1 store,IDeribitFixNativeReportJournal journal,OmsPhysicalSessionAuthority authority,
        OmsPrivateCaptureCoordinator coordinator,OmsPrivateCaptureSession captureSession,HttpClient http,
        Func<OmsJournalCommandRequest,CancellationToken,Task<DeribitNativeCommandView?>> parent,Action recovery,CancellationToken ct=default)
        =>OpenCoreAsync(binding,options,store,journal,authority,()=>coordinator.BeginExternalIngress(captureSession),http,parent,recovery,
            async token=>await DeribitFixTlsWireV1.OpenAsync(options,token),ct);

    internal static async Task<DeribitOmsFixTransportV1> OpenCoreAsync(DeribitPrivateCaptureBinding binding,DeribitFixOptionsV1 options,
        IOmsFixSessionStoreV1 store,IDeribitFixNativeReportJournal journal,OmsPhysicalSessionAuthority authority,
        Func<OmsExternalEconomicIngress> ingress,HttpClient http,
        Func<OmsJournalCommandRequest,CancellationToken,Task<DeribitNativeCommandView?>> parent,Action recovery,
        Func<CancellationToken,Task<IDeribitFixWireV1>> openWire,CancellationToken ct)
    {
        options.Validate();_ = binding.Plan.Validate(binding.Options,binding.Scope);
        if(binding.Scope!=options.Session.Scope||binding.Plan.IsSubaccount||http.DefaultRequestHeaders.Authorization is not null||http.DefaultRequestHeaders.Contains("Cookie"))
            throw new InvalidOperationException("Pinned main-account FIX transport required.");
        var t=new DeribitOmsFixTransportV1(binding,options,http,journal,parent,recovery);
        try
        {
            _=await t.PinnedRpc(ct);
            t.wire=await openWire(ct);
            t.engine=new(options,store,authority,binding.Options.ClientId,binding.Options.ClientSecret,
                bytes=>t.wire.WriteAsync(bytes,t.stop.Token),t.Report,t.recovery,t.Abort,ingress);
            t.reader=Task.Run(t.ReadLoop);t.ticker=Task.Run(t.TickLoop);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(10));
            while(!t.engine.LoggedOn){t.EnsureRunning();await Task.Delay(10,deadline.Token);}
            return t;
        }
        catch{await t.DisposeAsync();throw new InvalidOperationException("Owned FIX transport startup refused.");}
    }

    private async Task<DeribitRecoveryRpc> PinnedRpc(CancellationToken ct)
    {
        var rpc=new DeribitRecoveryRpc(http,binding.Scope.Environment);var name="session:fix_pin_"+Guid.NewGuid().ToString("N");
        var auth=await rpc.Call("public/auth",new{grant_type="client_credentials",client_id=binding.Options.ClientId,
            client_secret=binding.Options.ClientSecret,scope=name+" expires:300 trade:read account:read wallet:none block_trade:none block_rfq:none"},ct);
        var token=auth.GetProperty("access_token").GetString();
        if(!DeribitRecoveryQueries.ValidGrant(auth.GetProperty("scope").GetString()!,name)||auth.GetProperty("expires_in").GetInt32() is <120 or >300||
            string.IsNullOrWhiteSpace(token)||token.Length>8192||token.Any(char.IsControl))throw new InvalidOperationException("FIX account pin grant refused.");
        rpc.Token=token;
        var summary=await rpc.Call("private/get_account_summary",new{currency=binding.Options.AccountCurrency,extended=true},ct);
        if(summary.GetProperty("id").GetInt64()!=binding.Plan.ExpectedAccountId||summary.GetProperty("type").GetString()!="main"||
            summary.GetProperty("currency").GetString()!=binding.Options.AccountCurrency||summary.GetProperty("cross_collateral_enabled").GetBoolean()||
            summary.GetProperty("portfolio_margining_enabled").GetBoolean())throw new InvalidOperationException("FIX native account pin mismatch.");
        return rpc;
    }

    public async Task<DeribitFixCommandBindingV1> PrepareAsync(OmsJournalCommandRequest command,CancellationToken ct)
    {
        EnsureRunning();command.Validate();
        if(command.Scope!=binding.Scope||!binding.Plan.Instruments.TryGetValue(command.Payload.Instrument,out var allowed))throw new InvalidOperationException("FIX instrument outside pinned manifest.");
        var rpc=await PinnedRpc(ct);
        var m=await rpc.Call("public/get_instrument",new{instrument_name=command.Payload.Instrument},ct);
        var observed=DateTimeOffset.UtcNow;
        var metadata=new DeribitFixInstrumentV1(m.GetProperty("instrument_name").GetString()!,m.GetProperty("kind").GetString()!,
            m.GetProperty("instrument_type").GetString()!,m.GetProperty("settlement_currency").GetString()!,m.GetProperty("quote_currency").GetString()!,
            m.GetProperty("contract_size").GetDecimal(),m.GetProperty("min_trade_amount").GetDecimal(),observed,
            DateTimeOffset.FromUnixTimeMilliseconds(m.GetProperty("expiration_timestamp").GetInt64()));
        if(!m.GetProperty("is_active").GetBoolean()||m.GetProperty("base_currency").GetString()!=metadata.Currency||metadata.Currency!=allowed.Currency||
            allowed.AmountUnit!=OmsPrivateAmountUnit.Usd||allowed.AmountCurrency!="USD")throw new InvalidOperationException("Unsupported native FIX instrument metadata.");
        DeribitFixCancelBindingV1? cancel=null;
        OmsJournalCommandRequest? parentCommand=null;
        if(command.Payload.Kind is OmsJournalCommandKind.Cancel or OmsJournalCommandKind.Replace)
        {
            var p=await parent(command,ct);
            if(p?.FixReport is null)throw new InvalidOperationException("FIX cancellation needs its native placement binding.");
            p.Validate();parentCommand=p.Command;cancel=new(p.Command,p.FixReport);
        }
        _=DeribitFixCommandEncoderV1.Encode(command,options.Session,metadata,observed,cancel);
        var version=command.Payload.Kind==OmsJournalCommandKind.Replace||parentCommand?.Payload.Kind==OmsJournalCommandKind.Replace?2:1;
        var dispatch=new DeribitFixCommandBindingV1(version,command,options.Session,engine.Epoch,observed,metadata.Currency,metadata.ContractUsd,
            DeribitFixCommandBindingV1.ClientId(command),cancel?.Acknowledgement);dispatch.Validate();
        lock(sync)
        {if(response is not null)throw new InvalidOperationException("FIX dispatch already active.");prepared=dispatch;instrument=metadata;original=parentCommand;}
        return dispatch;
    }

    public Task<DeribitJsonRpcCommandReceipt> SendAsync(OmsJournalCommandRequest command,OmsPhysicalWritePermit permit,CancellationToken ct)
        =>throw new InvalidOperationException("FIX never fabricates JSON-RPC evidence.");

    public async Task<DeribitFixExecutionReportV1> SendNativeAsync(OmsJournalCommandRequest command,OmsPhysicalWritePermit permit,CancellationToken ct)
    {
        Task<DeribitFixExecutionReportV1> waiting;DeribitFixCommandBindingV1 dispatch;DeribitFixInstrumentV1 metadata;OmsJournalCommandRequest? owner;
        lock(sync)
        {
            EnsureRunning();dispatch=prepared??throw new InvalidOperationException("Durable FIX preparation required.");
            if(dispatch.Command!=command||response is not null)throw new InvalidOperationException("FIX command binding changed.");
            metadata=instrument!;owner=original;response=new(TaskCreationOptions.RunContinuationsAsynchronously);waiting=response.Task;
        }
        try
        {
            if(command.Payload.Provenance==DeribitFixQualificationScenarioV1.Provenance&&command.Payload.Kind!=OmsJournalCommandKind.Cancel)
            {
                var quote=await new DeribitFixQualificationMarketV1(binding,http).ReadAsync(ct);
                _=DeribitFixQualificationScenarioV1.Price(quote,DateTimeOffset.UtcNow);
                if(quote.Scope!=command.Scope||command.Payload.Quantity!=1||!command.Payload.PostOnly||command.Payload.ReduceOnly||command.Payload.Side!=TradeOps.Domain.Enums.OrderSide.Buy||
                    command.Payload.LimitPrice>quote.Bid*.90m||command.Payload.LimitPrice<quote.MinPrice||command.Payload.LimitPrice>quote.MaxPrice)
                    throw new InvalidOperationException("Qualification distance/size changed before physical I/O.");
            }
            engine.SendCommand(command,permit,metadata,owner is null?null:new(owner,dispatch.Parent!));
            return await waiting.WaitAsync(ct);
        }
        catch{Abort();throw;}
        finally{lock(sync){response=null;prepared=null;instrument=null;original=null;}}
    }

    private void Report(string raw,Guid epoch,DateTimeOffset received)
    {
        var m=new QuickFix.Message();m.FromString(raw,false,null,null);
        var report=m.Header.GetString(35)=="8"?new DeribitFixReportEnvelopeV1(1,DeribitFixReportParserV1.Execution(m,options.Session,epoch,received),null):
            new(1,null,DeribitFixReportParserV1.CancelReject(m,options.Session,epoch,received));
        if(journal.Record(report)==DeribitFixReportWriteResult.Collision)throw new InvalidOperationException("Native FIX collision.");
        lock(sync)
        {
            if(report.Execution is{} e&&prepared?.Match(e) is not null)response?.TrySetResult(e);
            if(report.CancelReject is not null&&response is not null)response.TrySetException(new InvalidOperationException("Native cancel outcome requires recovery."));
        }
    }
    private async Task ReadLoop()
    {
        try{var buffer=new byte[32768];while(!stop.IsCancellationRequested){var n=await wire.ReadAsync(buffer,stop.Token);if(n==0)throw new IOException("FIX disconnected.");engine.Receive(buffer.AsSpan(0,n));}}
        catch{Abort();throw;}
    }
    private async Task TickLoop()
    {
        try{while(!stop.IsCancellationRequested){EnsureRunning();engine.Tick();await Task.Delay(100,stop.Token);}}
        catch{Abort();throw;}
    }
    private void EnsureRunning()
    {if(stop.IsCancellationRequested||DateTimeOffset.UtcNow>=expires||reader.IsFaulted||ticker.IsFaulted)throw new InvalidOperationException("FIX supervision or session lifetime ended.");}
    public bool IsRunning => !stop.IsCancellationRequested&&!reader.IsFaulted&&!ticker.IsFaulted&&DateTimeOffset.UtcNow<expires;
    public void Abort()
    {if(!stop.IsCancellationRequested){stop.Cancel();wire?.Abort();recovery();lock(sync)response?.TrySetException(new IOException("FIX transport stopped."));}}
    public async ValueTask DisposeAsync()
    {
        Abort();foreach(var task in new[]{reader,ticker})try{await task;}catch{}
        engine?.Dispose();if(wire is not null)await wire.DisposeAsync();
    }
}
