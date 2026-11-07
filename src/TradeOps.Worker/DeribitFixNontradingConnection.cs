using System.Net;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Connectors.Deribit.Fix;
using TradeOps.Infrastructure.Persistence.Oms;

namespace TradeOps.Worker;

/// <summary>Exclusive console branch: no hosted services, execution gateway or order-intent API.
/// Real constrained connections and the same physical file owner remain mandatory.</summary>
public static class DeribitFixNontradingConnection
{
    public const string Mode="DeribitFixNontradingConnection";
    public static void ValidateConfiguration(IConfiguration config)=>_=Read(config);
    private sealed class Settings
    {
        internal required DeribitPrivateCaptureBinding Binding;
        internal required OmsFixSessionKeyV1 Session;
        internal required string Root;
        internal required Dictionary<string,NpgsqlConnectionStringBuilder> Connections;
    }
    private static Settings Read(IConfiguration config)
    {
        if(config["Worker:Mode"]!=Mode||config["DeribitOms:Enabled"]!="false"||config["DeribitFix:Enabled"]!="true")throw new InvalidOperationException("Explicit nontrading FIX mode required.");
        var binding=DeribitCaptureStartup.ReadBinding(config);var root=config["DeribitOms:LockRoot"];
        if(string.IsNullOrWhiteSpace(root)||!Path.IsPathFullyQualified(root)||!Directory.Exists(root))throw new InvalidOperationException("Provisioned physical owner required.");
        var key=new OmsFixSessionKeyV1(binding.Plan.IsSubaccount?2:1,binding.Scope,config["DeribitFix:SenderCompId"]??"","DERIBITSERVER");key.Validate();
        var connections=new Dictionary<string,NpgsqlConnectionStringBuilder>(StringComparer.Ordinal);
        foreach(var name in new[]{"Capture","Recovery","Execution","Fix"})
        {
            var b=new NpgsqlConnectionStringBuilder(config.GetConnectionString("TradeOps"+name+"Db")??throw new InvalidOperationException());
            if(string.IsNullOrWhiteSpace(b.Host)||string.IsNullOrWhiteSpace(b.Database)||string.IsNullOrWhiteSpace(b.Username)||!string.IsNullOrEmpty(b.Options))throw new InvalidOperationException("Explicit database identities required.");
            b.SearchPath="public";b.IncludeErrorDetail=false;b.LogParameters=false;b.Pooling=true;b.MinPoolSize=0;b.MaxPoolSize=4;b.Multiplexing=false;b.Timeout=5;b.CommandTimeout=10;
            connections.Add(name,b);
        }
        if(connections.Values.Select(x=>(x.Host,x.Port,x.Database)).Distinct().Count()!=1||connections.Values.Select(x=>x.Username).Distinct(StringComparer.Ordinal).Count()!=4)
            throw new InvalidOperationException("Four separate identities in the authoritative store required.");
        return new(){Binding=binding,Session=key,Root=root,Connections=connections};
    }
    public static async Task<DeribitFixConnectionProbeResultV1> RunAsync(IConfiguration config,CancellationToken ct=default)
    {
        var sources=new Dictionary<string,NpgsqlDataSource>();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(55));var token=deadline.Token;
        try
        {
            var settings=Read(config);foreach(var (name,b)in settings.Connections)sources.Add(name,NpgsqlDataSource.Create(b.ConnectionString));
            await OmsPostgresPrivateCaptureOwner.ValidateRoleAsync(sources["Capture"],token);
            await OmsPostgresRecoveryRuntimeGuard.ValidateAsync(sources["Recovery"],token);
            await OmsPostgresExecutionRuntimeGuard.ValidateAsync(sources["Execution"],token,settings.Binding.Scope);
            await OmsPostgresFixRuntimeGuardV1.ValidateAsync(sources["Fix"],token);
            await using var owner=await OmsSingleHostPhysicalFence.AcquireAsync(sources["Execution"],settings.Binding.Scope,settings.Root,token);
            await using var capture=await OmsPostgresPrivateCaptureOwner.AcquireAsync(sources["Capture"],settings.Binding.Scope,token);
            await using var coordinator=new OmsPrivateCaptureCoordinator(capture,new());var session=await coordinator.SwitchAsync(settings.Binding.Scope,token);
            using var protocol=new OmsPostgresFixSessionStoreV1(sources["Fix"],settings.Session);
            using var handler=new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,UseDefaultCredentials=false,AutomaticDecompression=DecompressionMethods.None};
            using var http=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(10)};
            return await DeribitFixConnectionProbeV1.RunAsync(settings.Binding,new(settings.Session),protocol,new OmsPostgresDeribitFixReportJournalV1(sources["Fix"],settings.Session),
                owner.CreateSessionAuthority(),coordinator,session,http,()=>{},token);
        }
        catch{return new(1,"FIX_NONTRADING_CONNECTION_REFUSED",false,false,false,false,false,0);}
        finally{foreach(var source in sources.Values)await source.DisposeAsync();}
    }
}
