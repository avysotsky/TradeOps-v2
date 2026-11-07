using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit.Fix;
using TradeOps.Infrastructure.Persistence.Oms;

namespace TradeOps.Worker;

public sealed class DeribitFixQualificationWorker(DeribitOmsExecutionSource execution,DeribitAccountRecoveryWorker recovery,
    DeribitOmsExecutionWorker gateway,DeribitFixQualificationHostOptions options,IHttpClientFactory http,IHostApplicationLifetime lifetime):BackgroundService
{
    public DeribitFixQualificationResultV1? Result{get;private set;}
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);deadline.CancelAfter(TimeSpan.FromSeconds(150));
        var ct=deadline.Token;
        try
        {
            async Task<OmsRecoveredAccountStateV1> Current(CancellationToken token)
            {
                while(true)
                {
                    token.ThrowIfCancellationRequested();var live=recovery.Current;
                    if(gateway.IsAvailable&&live is not null)
                    {
                        var state=await recovery.ReadCurrentAsync(token);
                        if(state is not null&&state.RunId==live.Qualified.Start.RunId&&ReferenceEquals(live,recovery.Current)&&live.Qualified.IsCurrent)return state;
                    }
                    await Task.Delay(50,token);
                }
            }
            var initial=await Current(ct);
            await using var lease=await OmsPostgresFixQualificationLeaseV1.AcquireAsync(execution.Source,initial.Scope,ct);
            using var client=http.CreateClient("DeribitAccountRecovery");
            var scenario=new DeribitFixQualificationScenarioV1(gateway,new DeribitFixQualificationMarketV1(options.Binding,client),Current,lease);
            Result=await scenario.RunAsync(ct);
        }
        catch{Result=new("FIX_QUALIFICATION_REFUSED",false,false,false,false,null,true,false,0);}
        finally{lifetime.StopApplication();}
    }
}

public sealed record DeribitFixQualificationHostOptions(TradeOps.Connectors.Deribit.DeribitPrivateCaptureBinding Binding);
