using TradeOps.Application.Oms;

namespace TradeOps.Connectors.Deribit.Fix;

public sealed record DeribitFixConnectionProbeResultV1(int SchemaVersion,string Status,bool NativeBindingVerified,bool TlsConnected,
    bool LogonAccepted,bool HeartbeatVerified,bool LogoutAccepted,int BusinessReportsReceived)
{public int BusinessMessagesSent=>0;public bool TradingReady=>false;public bool TradingStarted=>false;}

/// <summary>Owned administrative connection only. Uses the existing engine, persistent protocol
/// store/journal and physical owner. No command API, gateway, economic permit, reset or retries.</summary>
public static class DeribitFixConnectionProbeV1
{
    public static Task<DeribitFixConnectionProbeResultV1> RunAsync(DeribitPrivateCaptureBinding binding,DeribitFixOptionsV1 options,
        IOmsFixSessionStoreV1 store,IDeribitFixNativeReportJournal journal,OmsPhysicalSessionAuthority authority,
        OmsPrivateCaptureCoordinator coordinator,OmsPrivateCaptureSession captureSession,HttpClient http,Action recovery,CancellationToken ct=default)
    {
        if(captureSession.Scope!=binding.Scope)throw new ArgumentException("Same pinned native ingress scope required.");
        return RunCoreAsync(binding,options,store,journal,authority,()=>coordinator.BeginExternalIngress(captureSession),http,recovery,
            async token=>await DeribitFixTlsWireV1.OpenAsync(options,token),ct);
    }

    internal static async Task<DeribitFixConnectionProbeResultV1> RunCoreAsync(DeribitPrivateCaptureBinding binding,DeribitFixOptionsV1 options,
        IOmsFixSessionStoreV1 store,IDeribitFixNativeReportJournal journal,OmsPhysicalSessionAuthority authority,
        Func<OmsExternalEconomicIngress> ingress,HttpClient http,Action recovery,Func<CancellationToken,Task<IDeribitFixWireV1>> openWire,CancellationToken ct)
    {
        options.Validate();_=binding.Plan.Validate(binding.Options,binding.Scope);
        if(binding.Scope!=options.Session.Scope||options.Session.SchemaVersion!=(binding.Plan.IsSubaccount?2:1))throw new ArgumentException("Pinned standalone FIX scope required.");
        var verified=false;var tls=false;var logon=false;var heartbeat=false;var logout=false;var reports=0;var closing=0;var logoutInitiated=0;
        var ack=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var closed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fault=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var nonce=Guid.NewGuid().ToString("N");
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(ct);stop.CancelAfter(TimeSpan.FromSeconds(35));var token=stop.Token;
        DeribitFixAdminOnlyWireV1? wire=null;DeribitQuickFixSessionV1? engine=null;Task reader=Task.CompletedTask,ticker=Task.CompletedTask;
        var initialSender=store.NextSender;var initialTarget=store.NextTarget;
        ulong receivedHeartbeat=0,receivedLogout=0;
        try
        {
            _=await DeribitAccountModeReaderV1.ReadAsync(binding,http,token);verified=true;
            wire=new(await openWire(token),options.MaximumFrameBytes,type=>{if(type=="5")Interlocked.Exchange(ref logoutInitiated,1);});tls=true;
            engine=new(options,store,authority,binding.Options.ClientId,binding.Options.ClientSecret,
                frame=>wire.WriteAsync(frame,token),Report,recovery,wire.Abort,ingress,Admin);
            reader=Task.Run(async()=>
            {
                try
                {
                    var buffer=new byte[32768];while(!token.IsCancellationRequested)
                    {var n=await wire.ReadAsync(buffer,token);if(n==0)throw new IOException();engine.Receive(buffer.AsSpan(0,n));}
                }
                catch{if(!closed.Task.IsCompletedSuccessfully)fault.TrySetResult();}
            },CancellationToken.None);
            ticker=Task.Run(async()=>
            {
                try{while(!token.IsCancellationRequested){engine.Tick();await Task.Delay(50,token);}}
                catch{if(!closed.Task.IsCompletedSuccessfully)fault.TrySetResult();}
            },CancellationToken.None);
            while(!engine.LoggedOn){if(fault.Task.IsCompleted)throw new InvalidOperationException();await Task.Delay(10,token);}
            logon=true;engine.RequestTest(nonce);await Await(ack.Task);heartbeat=true;
            Interlocked.Exchange(ref closing,1);engine.RequestLogout();await Await(closed.Task);logout=true;
            await Task.WhenAll(reader,ticker).WaitAsync(TimeSpan.FromSeconds(3),token);
            if(fault.Task.IsCompleted||engine.HasGap||wire.BusinessBlocked!=0||reports!=0||wire.Logons!=1||wire.TestRequests<1||wire.Logouts!=1||
                receivedHeartbeat==0||receivedLogout==0||store.NextTarget<=receivedHeartbeat||store.NextTarget<=receivedLogout||
                store.NextSender<initialSender+3||store.NextTarget<initialTarget+3)throw new InvalidOperationException();
            return Result(true);
        }
        catch{return Result(false);}
        finally
        {
            stop.Cancel();wire?.Abort();foreach(var task in new[]{reader,ticker})try{await task;}catch{}
            engine?.Dispose();if(wire is not null)await wire.DisposeAsync();
        }
        async Task Await(Task target)
        {var done=await Task.WhenAny(target,fault.Task).WaitAsync(token);if(done!=target)throw new InvalidOperationException();await target;}
        void Admin(DeribitFixAdminObservationV1 value)
        {
            if(value.Type=="0"&&value.TestRequestId==nonce){receivedHeartbeat=value.Sequence;ack.TrySetResult();}
            if(value.Type=="5"&&Volatile.Read(ref closing)!=0&&Volatile.Read(ref logoutInitiated)!=0){receivedLogout=value.Sequence;closed.TrySetResult();}
        }
        void Report(string raw,Guid epoch,DateTimeOffset received)
        {
            var message=new QuickFix.Message();message.FromString(raw,false,null,null);
            var envelope=message.Header.GetString(35)=="8"?new DeribitFixReportEnvelopeV1(1,DeribitFixReportParserV1.Execution(message,options.Session,epoch,received),null):
                new(1,null,DeribitFixReportParserV1.CancelReject(message,options.Session,epoch,received));
            _=journal.Record(envelope);Interlocked.Increment(ref reports);throw new InvalidOperationException("Business report requires recovery outside the connection probe.");
        }
        DeribitFixConnectionProbeResultV1 Result(bool passed)=>new(1,passed?"FIX_NONTRADING_CONNECTION_PASS":"FIX_NONTRADING_CONNECTION_REFUSED",verified,tls,logon,heartbeat,logout,reports);
    }
}
