using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;

// Offline review artifacts only. No DB credentials, venue client, Apply or trading mode.
if(args.Length!=2){Console.WriteLine("{\"Status\":\"REVIEW_INPUT_REQUIRED\"}");return 1;}
try
{
    using var input=JsonDocument.Parse(File.ReadAllText(args[0]));var data=input.RootElement;
    if(!data.GetProperty("readonly").GetBoolean()||data.GetProperty("scopes").GetArrayLength()!=1)throw new ArgumentException();
    var s=data.GetProperty("scopes")[0];
    var scope=new AccountKey(new(s.GetProperty("tenant_id").GetString()!),new(s.GetProperty("venue_id").GetString()!),
        (OmsEnvironment)s.GetProperty("environment").GetInt32(),new(s.GetProperty("account_id").GetString()!),
        s.GetProperty("subaccount_id").GetString() is{} sub&&sub!=""?new TradingSubaccountId(sub):null);
    var plan=OmsFixProvisioningReviewPlan.Generate(data.GetProperty("migrations").EnumerateArray().Select(x=>x.GetString()!).ToArray(),scope,
        "tradeops_mainnet_qualification","tradeops-mainnet-qualification:v1","tradeops_mainnet_qualification_v1");
    var directory=Path.GetFullPath(args[1]);Directory.CreateDirectory(directory);
    var files=new Dictionary<string,byte[]> { ["01-migrations.review.sql"]=Encoding.UTF8.GetBytes(plan.MigrationSql),["02-roles-session.review.sql"]=Encoding.UTF8.GetBytes(plan.RolesAndSessionSql) };
    foreach(var file in files){using var stream=new FileStream(Path.Combine(directory,file.Key),FileMode.CreateNew,FileAccess.Write,FileShare.None);stream.Write(file.Value);}
    var summary=new{Status="REVIEW_ONLY",plan.FromMigration,plan.ToMigration,plan.MigrationCount,RuntimeRoles=OmsFixProvisioningReviewPlan.Roles,
        RolesCreatedAs="NOLOGIN",ExistingJournalRewritten=false,RiskPolicySeeded=false,PhysicalBindingSeeded=false,DatabaseUsed=false,TradingStarted=false,
        Files=files.ToDictionary(x=>x.Key,x=>Convert.ToHexString(SHA256.HashData(x.Value)).ToLowerInvariant())};
    var json=JsonSerializer.Serialize(summary,new JsonSerializerOptions{WriteIndented=true});
    using(var stream=new FileStream(Path.Combine(directory,"review-manifest.json"),FileMode.CreateNew,FileAccess.Write,FileShare.None))stream.Write(Encoding.UTF8.GetBytes(json));
    Console.WriteLine(JsonSerializer.Serialize(summary));return 0;
}
catch(Exception error){Console.WriteLine(JsonSerializer.Serialize(new{Status="REVIEW_GENERATION_REFUSED",Reason=error.GetType().Name,DatabaseUsed=false,TradingStarted=false}));return 1;}
