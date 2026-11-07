using System.Globalization;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;

namespace TradeOps.Worker;

public sealed record DeribitFixConfigurationCheckResultV1(int SchemaVersion,string Status,IReadOnlyList<OmsFixConfigurationFindingV1> Checks,
    bool VaultUsed=false,bool VenueUsed=false,bool TradingStarted=false,bool LiveAuthorityEstablished=false,bool NativeAccountVerified=false);

public static class DeribitFixConfigurationCheck
{
    public const string Mode="DeribitFixConfigurationCheck";
    public static IReadOnlyList<string> RequiredKeys {get;}=Array.AsReadOnly<string>(["ConnectionStrings:TradeOpsCaptureDb","ConnectionStrings:TradeOpsRecoveryDb",
        "ConnectionStrings:TradeOpsExecutionDb","ConnectionStrings:TradeOpsFixDb","DeribitOms:LockRoot","DeribitFix:SenderCompId",
        "DeribitCapture:Tenant","DeribitCapture:Account","DeribitRecovery:InitialFromUtc"]);
    public static async Task<DeribitFixConfigurationCheckResultV1> RunAsync(IConfiguration config,CancellationToken ct=default)
    {
        var checks=RequiredKeys.Select(k=>new OmsFixConfigurationFindingV1("Setting:"+k,!string.IsNullOrWhiteSpace(config[k]))).ToList();
        if(checks.Any(x=>!x.Passed))return Result();
        var sources=new Dictionary<string,NpgsqlDataSource>();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var sub=config["DeribitCapture:Subaccount"];
            var scope=new AccountKey(new(config["DeribitCapture:Tenant"]!),new("DERIBIT"),OmsEnvironment.Mainnet,new(config["DeribitCapture:Account"]!),sub is null?null:new TradingSubaccountId(sub));
            var session=new OmsFixSessionKeyV1(sub is null?1:2,scope,config["DeribitFix:SenderCompId"]!,"DERIBITSERVER");session.Validate();
            if(!DateTimeOffset.TryParse(config["DeribitRecovery:InitialFromUtc"],CultureInfo.InvariantCulture,DateTimeStyles.None,out var from)||
                from.Offset!=TimeSpan.Zero||from>DateTimeOffset.UtcNow.AddSeconds(-1)||from<DateTimeOffset.UtcNow.AddDays(-7))throw new InvalidOperationException();
            if(config["DeribitRecovery:HistoryCoverage"] is{} coverage&&coverage!=DeribitCollateralHistoryCoverageV2.PolicyName)throw new InvalidOperationException();
            if(!Path.IsPathFullyQualified(config["DeribitOms:LockRoot"]!)||!Directory.Exists(config["DeribitOms:LockRoot"]))throw new InvalidOperationException();
            var builders=new List<NpgsqlConnectionStringBuilder>();
            foreach(var name in new[]{"Capture","Recovery","Execution","Fix"})
            {
                var b=new NpgsqlConnectionStringBuilder(config.GetConnectionString("TradeOps"+name+"Db"));
                // Startup SQL supplied by a caller must never precede the read-only setting.
                if(string.IsNullOrWhiteSpace(b.Host)||string.IsNullOrWhiteSpace(b.Database)||string.IsNullOrWhiteSpace(b.Username)||!string.IsNullOrEmpty(b.Options))throw new InvalidOperationException();
                builders.Add(b);
                b.Options="-c default_transaction_read_only=on";b.Pooling=false;b.Multiplexing=false;b.SearchPath="public";
                b.IncludeErrorDetail=false;b.LogParameters=false;b.Timeout=5;b.CommandTimeout=10;
                sources.Add(name,NpgsqlDataSource.Create(b.ConnectionString));
            }
            if(builders.Select(x=>(x.Host,x.Port,x.Database)).Distinct().Count()!=1||builders.Select(x=>x.Username).Distinct(StringComparer.Ordinal).Count()!=4)throw new InvalidOperationException();
            checks.Add(new("ConfigurationShape",true));
            checks.AddRange(await OmsPostgresFixConfigurationInspectionV1.InspectAsync(sources,scope,session,config["DeribitOms:LockRoot"]!,deadline.Token));
        }
        catch{checks.Add(new("ConfigurationShapeOrInspection",false));}
        finally{foreach(var source in sources.Values)await source.DisposeAsync();}
        return Result();
        DeribitFixConfigurationCheckResultV1 Result()=>new(1,checks.All(x=>x.Passed)?"FIX_CONFIGURATION_CHECK_PASS":"FIX_CONFIGURATION_CHECK_BLOCKED",checks);
    }
}
