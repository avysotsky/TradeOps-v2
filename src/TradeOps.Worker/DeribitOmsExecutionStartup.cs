using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Infrastructure.Persistence.Oms;

namespace TradeOps.Worker;

public sealed record DeribitOmsExecutionOptions(string LockRoot);
public sealed class DeribitOmsExecutionSource(NpgsqlDataSource source):IDisposable
{ public NpgsqlDataSource Source { get; }=source; public void Dispose()=>Source.Dispose(); }
public static class DeribitOmsExecutionStartup
{
    public const string Mode="DeribitOmsExecution";
    public static bool TryConfigure(HostApplicationBuilder builder)
    {
        if(builder.Configuration["Worker:Mode"]!=Mode) return false;
        if(!builder.Configuration.GetValue<bool>("DeribitOms:Enabled")) throw new InvalidOperationException("Explicit Deribit OMS activation required.");
        var root=builder.Configuration["DeribitOms:LockRoot"];
        if(string.IsNullOrWhiteSpace(root)||!Path.IsPathFullyQualified(root)||!Directory.Exists(root)) throw new InvalidOperationException("Provisioned physical owner root required.");
        DeribitRecoveryStartup.TryConfigure(builder);
        if(DeribitCaptureStartup.ReadBinding(builder.Configuration).Plan.IsSubaccount)throw new InvalidOperationException("OMS execution v1 supports a pinned main account only.");
        NpgsqlConnectionStringBuilder execution;
        try
        {
        var raw=builder.Configuration.GetConnectionString("TradeOpsExecutionDb") ?? throw new InvalidOperationException("Execution database identity missing.");
        execution=new NpgsqlConnectionStringBuilder(raw) { Pooling=true,MinPoolSize=0,MaxPoolSize=20,Multiplexing=false,SearchPath="public",IncludeErrorDetail=false,LogParameters=false,Timeout=10,CommandTimeout=15 };
        foreach(var name in new[]{"TradeOpsCaptureDb","TradeOpsRecoveryDb"})
        {
            var other=new NpgsqlConnectionStringBuilder(builder.Configuration.GetConnectionString(name));
            if(execution.Host!=other.Host||execution.Port!=other.Port||execution.Database!=other.Database||execution.Username==other.Username)
                throw new InvalidOperationException("Execution needs a separate identity in the same authoritative OMS database.");
        }
        }
        catch(Exception error) when(error is ArgumentException or InvalidOperationException)
        {throw new InvalidOperationException("Invalid execution database identity or authority binding; startup refused.");}
        builder.Services.AddSingleton(new DeribitOmsExecutionOptions(root));
        builder.Services.AddSingleton(_=>new DeribitOmsExecutionSource(NpgsqlDataSource.Create(execution.ConnectionString)));
        builder.Services.AddSingleton<IOmsExecutionRecoverySource>(sp=>sp.GetRequiredService<DeribitAccountRecoveryWorker>());
        builder.Services.AddSingleton<DeribitOmsExecutionWorker>();
        builder.Services.AddSingleton<IOmsOrderIntentGateway>(sp=>sp.GetRequiredService<DeribitOmsExecutionWorker>());
        builder.Services.AddHostedService(sp=>sp.GetRequiredService<DeribitOmsExecutionWorker>());
        return true;
    }
}

public sealed class DeribitOmsExecutionWorker(DeribitOmsExecutionSource store,IDeribitRecoveryBindingSource bindings,
    IOmsExecutionRecoverySource recovery,IHttpClientFactory http,DeribitOmsExecutionOptions options,
    IHostApplicationLifetime lifetime,DeribitAccountRecoveryWorker accountRecovery):BackgroundService,IOmsOrderIntentGateway
{
    private OmsPostgresExecutionGateway? gateway;
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        OmsPostgresExecutionGateway? owned=null;
        try
        {
            await OmsPostgresExecutionRuntimeGuard.ValidateAsync(store.Source,ct);
            var metadata=await bindings.ReadAsync(ct); var pin=metadata.Binding;
            var instruments=metadata.Manifest.Where(x=>x.Kind=="future"&&x.AmountUnit==OmsPrivateAmountUnit.Usd&&x.AmountCurrency=="USD"&&(x.Currency is "BTC" or "ETH"))
                .Select(x=>new DeribitPrivateInstrument(x.NativeInstrument,x.Kind,x.Currency,x.AmountUnit,x.AmountCurrency)).ToArray();
            var binding=new DeribitPrivateCaptureBinding(pin.Scope,pin.Options,new(pin.Plan.ExpectedAccountId,pin.Plan.IsSubaccount,instruments));
            await OmsPostgresExecutionRuntimeGuard.ValidateAsync(store.Source,ct,binding.Scope);
            var policies=new OmsPostgresRiskPolicyReader(store.Source);
            var policy=await policies.ReadAsync(binding.Scope,ct);
            if(policy is null||DateTimeOffset.UtcNow<policy.ValidFromUtc||DateTimeOffset.UtcNow>policy.ValidThroughUtc)
                throw new InvalidOperationException("Explicit current account policy required; no generic or qualification defaults.");
            if(policy.AllowedInstruments.Any(x=>!instruments.Any(m=>m.Name==x)))throw new InvalidOperationException("Policy contains an unsupported execution instrument.");
            using var client=http.CreateClient("DeribitAccountRecovery");
            var pending=new OmsPostgresPendingRiskReader(store.Source);
            var risk=new DeribitRiskInputsReader(policies,new DeribitRiskObservationReader(binding,client,pendingRisk:pending),pendingRisk:pending);
            owned=await OmsPostgresExecutionGateway.OpenAsync(binding.Scope,store.Source,recovery,risk,options.LockRoot,
                async token=>await DeribitOmsWebSocketTransport.OpenAsync(binding,token),cancelPolicies:policies,ct:ct);
            Volatile.Write(ref gateway,owned);
            var seen=Guid.Empty;
            while(!ct.IsCancellationRequested)
            {
                var live=recovery.Current;
                var status=accountRecovery.CaptureStatus;
                if(seen!=Guid.Empty && (status is null || status.Epoch!=seen || status.Phase!=OmsPrivateStreamPhase.Recovering ||
                    status.Problem!=OmsPrivateStreamProblem.AwaitingReconciliation))
                { await new OmsPostgresAutomaticRiskCircuit(store.Source).ActivateAsync(binding.Scope,"Capture gap, disconnect or epoch change circuit activated.",ct); seen=Guid.Empty; }
                if(live is not null)seen=live.Qualified.Start.Epoch;
                await Task.Delay(100,ct);
            }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
        catch { throw new InvalidOperationException("Deribit OMS execution stopped; no send authority available."); }
        finally { Volatile.Write(ref gateway,null); if(owned is not null) await owned.DisposeAsync(); }
    }
    public async Task<OmsExecutionResult> SubmitAsync(OmsJournalCommandRequest command,CancellationToken ct=default)
    {
        var current=Volatile.Read(ref gateway);
        if(current is null) return new(OmsExecutionResultKind.Denied,null,"OMS sender unavailable.");
        OmsExecutionResult result;
        try { result=await current.SubmitAsync(command,ct); }
        catch { lifetime.StopApplication(); throw new InvalidOperationException("OMS execution stopped; recovery required."); }
        if(result.Kind==OmsExecutionResultKind.Unknown) lifetime.StopApplication();
        return result;
    }
}
