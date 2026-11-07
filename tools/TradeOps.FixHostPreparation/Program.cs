using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;

namespace TradeOps.FixHostPreparation;

// No order runner, execution gateway, DB opener or Apply mode.
internal static class EntryPoint
{
    public static async Task<int> Main(string[] args)
    {
        if(args.Length!=2||args[0] is not("--read-account" or "--prepare-node" or "--read-cross-pm"))
        {Console.WriteLine("{\"Status\":\"PREPARATION_MODE_REQUIRED\",\"TradingStarted\":false}");return 1;}
        AccountInput? input=null;using var bytes=new MemoryStream();
        try
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));var ct=deadline.Token;
            using var stdin=Console.OpenStandardInput();var chunk=new byte[4096];
            try{int n;while((n=await stdin.ReadAsync(chunk,ct))!=0){if(bytes.Length+n>16384)throw new ArgumentException();bytes.Write(chunk,0,n);}}
            finally{CryptographicOperations.ZeroMemory(chunk);}
            input=JsonSerializer.Deserialize<AccountInput>(bytes.GetBuffer().AsSpan(0,(int)bytes.Length),new JsonSerializerOptions{MaxDepth=8,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow})??throw new ArgumentException();
            var scope=input.Scope();
            if(args[0]=="--prepare-node")
            {
                if(input.Purpose!="PREPARE_FIX_LOCAL_NODE_V1"||input.ClientId!=""||input.ClientSecret!="")throw new ArgumentException();
                var root=Path.GetFullPath(args[1]);if(root!=Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,".tradeops","oms-execution")))throw new ArgumentException();
                for(DirectoryInfo? d=new(root);d is not null;d=d.Parent)if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new ArgumentException();
                Directory.CreateDirectory(root);var node=OmsExecutionNodeProvisioning.Prepare(root,scope);
                Console.WriteLine(JsonSerializer.Serialize(node));return 0;
            }
            var path=Path.GetFullPath(args[1]);var folder=Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,".tradeops","fix-host"));
            var prefix=args[0]=="--read-cross-pm"?"crosspm":"account";
            if(Path.GetDirectoryName(path)!=folder||!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path),"^"+prefix+"-[a-f0-9]{32}\\.private\\.json$"))throw new ArgumentException();
            for(DirectoryInfo? d=new(folder);d is not null;d=d.Parent)if(!d.Exists||(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new ArgumentException();
            using var handler=new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false,UseDefaultCredentials=false,AutomaticDecompression=DecompressionMethods.None};
            using var http=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(10)};
            if(args[0]=="--read-cross-pm")
            {
                AccountInspection.ValidateInput(input,"READ_ONLY_FIX_CROSS_PM_V1");
                var binding=AccountInspection.Binding(input);
                var preview=await DeribitCrossPortfolioPreviewReaderV1.ReadAsync(binding,http,[-1m,0m,1m],ct);
                using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))await JsonSerializer.SerializeAsync(file,preview,cancellationToken:ct);
                Console.WriteLine(JsonSerializer.Serialize(new{SchemaVersion=1,Status="NATIVE_CROSS_PM_PREVIEW_PASS",MarginModel="cross_pm",NativeBindingVerified=true,
                    NativeTotalEquityUsd=preview.After.EquityUsd,NativeMarginBalanceUsd=preview.After.MarginBalanceUsd,NativeInitialMarginUsd=preview.After.InitialMarginUsd,
                    ScenarioCount=preview.Scenarios.Count,MinimumSimulatedMarginUsd=preview.Scenarios.Min(x=>x.Totals.InitialMarginUsd),MaximumSimulatedMarginUsd=preview.Scenarios.Max(x=>x.Totals.InitialMarginUsd),
                    IndexCount=preview.IndexPrices.Count,ObservedUtc=preview.CompletedUtc,BusinessMessagesSent=0,AccountReady=false,TradingReady=false,TradingStarted=false,DatabaseUsed=false}));return 0;
            }
            var result=await AccountInspection.CollectAsync(input,http,ct);
            if(result.Inventory is not null)
            {using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);await JsonSerializer.SerializeAsync(file,result, cancellationToken:ct);}
            Console.WriteLine(JsonSerializer.Serialize(result.Diagnostics));return result.Diagnostics.Status=="PINNED_ACCOUNT_INVENTORY_PASS"?0:1;
        }
        catch(DeribitCrossPortfolioReadException error){Console.WriteLine(JsonSerializer.Serialize(new{Status="NATIVE_CROSS_PM_PREVIEW_REFUSED",Stage=error.Stage,VenueCode=error.VenueCode,TradingStarted=false,DatabaseUsed=false}));return 1;}
        catch{Console.WriteLine("{\"Status\":\"HOST_PREPARATION_REFUSED\",\"TradingStarted\":false}");return 1;}
        finally{if(input is not null){input.ClientId="";input.ClientSecret="";}CryptographicOperations.ZeroMemory(bytes.GetBuffer());}
    }
}

internal sealed class AccountInput
{
    public string Purpose{get;set;}="";public string Environment{get;set;}="";public string Tenant{get;set;}="";
    public long ExpectedAccountId{get;set;} public string AccountType{get;set;}="";
    public string ClientId{get;set;}="";public string ClientSecret{get;set;}="";
    internal AccountKey Scope()
    {
        if(Environment!="Mainnet"||ExpectedAccountId<=0||AccountType is not("main" or "subaccount"))throw new ArgumentException();
        var label="observed-native:"+ExpectedAccountId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new(new(Tenant),new("DERIBIT"),OmsEnvironment.Mainnet,new(label),AccountType=="subaccount"?new TradingSubaccountId(label):null);
    }
}
internal sealed record AccountDiagnostics(string Status,bool NativeBindingVerified,int PositionCount,int OpenOrderCount,int CurrencyCount,
    bool CrossCollateralObserved,bool PortfolioMarginObserved,decimal? EstimatedEquityUsd,bool UsdEstimateComplete,DateTimeOffset ObservedUtc,
    string? EthMarginModel=null,decimal? NativeTotalEquityUsd=null)
{public int SchemaVersion=>2;public bool DatabaseUsed=>false;public bool TradingStarted=>false;public bool AccountReady=>false;}
internal sealed record AccountInspectionResult(AccountDiagnostics Diagnostics,DeribitAccountInventoryResult? Inventory,IReadOnlyDictionary<string,decimal> IndexPrices,
    DeribitAccountModeObservationV1? NativeMode=null);
internal static class AccountInspection
{
    internal static void ValidateInput(AccountInput input,string purpose)
    {
        if(input.Purpose!=purpose||string.IsNullOrWhiteSpace(input.ClientId)||input.ClientId.Length>512||input.ClientId.Any(char.IsControl)||
            string.IsNullOrWhiteSpace(input.ClientSecret)||input.ClientSecret.Length>4096||input.ClientSecret.Any(char.IsControl))throw new ArgumentException();
        _=input.Scope();
    }
    internal static DeribitPrivateCaptureBinding Binding(AccountInput input)=>new(input.Scope(),new(){WebSocketUrl="wss://www.deribit.com/ws/api/v2",ClientId=input.ClientId,ClientSecret=input.ClientSecret},
        DeribitPrivateCapturePlan.ForAccountRecovery(input.ExpectedAccountId,input.AccountType=="subaccount",[new("ETH-PERPETUAL","future","ETH",OmsPrivateAmountUnit.Usd,"USD")]));
    internal static async Task<AccountInspectionResult> CollectAsync(AccountInput input,HttpClient http,CancellationToken ct)
    {
        ValidateInput(input,"READ_ONLY_FIX_ACCOUNT_INVENTORY_V1");
        var scope=input.Scope();var binding=Binding(input);
        var inventory=await new DeribitAccountInventoryReader(binding,http).ReadAsync(new(scope,Guid.NewGuid(),TimeSpan.FromSeconds(45)),ct);
        if(!inventory.QueriesCompleted||!inventory.IsFreshAt(DateTimeOffset.UtcNow))
            return new(new("PINNED_ACCOUNT_INVENTORY_REFUSED",false,0,0,0,false,false,null,false,DateTimeOffset.UtcNow),null,new Dictionary<string,decimal>());
        var nativeMode=await DeribitAccountModeReaderV1.ReadAsync(binding,http,ct);
        var prices=new Dictionary<string,decimal>(StringComparer.Ordinal);decimal total=0;var complete=true;var rpcId=0;
        foreach(var balance in inventory.Balances.Where(x=>x.Equity!=0||x.CashBalance!=0))
        {
            if(balance.Currency is not("BTC" or "ETH")){complete=false;continue;}
            var id=++rpcId;using var request=new HttpRequestMessage(HttpMethod.Post,"https://www.deribit.com/api/v2")
                {Content=JsonContent.Create(new{jsonrpc="2.0",id,method="public/get_index_price",@params=new{index_name=balance.Currency.ToLowerInvariant()+"_usd"}})};
            using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
            if(response.RequestMessage?.RequestUri!=request.RequestUri||response.Content.Headers.ContentLength>32768)throw new InvalidOperationException();
            await using var stream=await response.Content.ReadAsStreamAsync(ct);using var data=new MemoryStream();var buffer=new byte[4096];int n;
            while((n=await stream.ReadAsync(buffer,ct))!=0){if(data.Length+n>32768)throw new InvalidOperationException();data.Write(buffer,0,n);}
            using var json=JsonDocument.Parse(data.GetBuffer().AsMemory(0,(int)data.Length));var r=json.RootElement;
            if(r.GetProperty("jsonrpc").GetString()!="2.0"||r.GetProperty("id").GetInt32()!=id||r.TryGetProperty("error",out _))throw new InvalidOperationException();
            var price=r.GetProperty("result").GetProperty("index_price").GetDecimal();if(price<=0)throw new InvalidOperationException();
            prices.Add(balance.Currency,price);total=checked(total+balance.Equity*price);
        }
        if(!inventory.IsFreshAt(DateTimeOffset.UtcNow))throw new InvalidOperationException();
        return new(new("PINNED_ACCOUNT_INVENTORY_PASS",true,inventory.Positions.Count,inventory.OpenOrders.Count,inventory.Balances.Count,
            inventory.Balances.Any(x=>x.CrossCollateralEnabled),inventory.Balances.Any(x=>x.PortfolioMarginingEnabled),complete?total:null,complete,DateTimeOffset.UtcNow,
            nativeMode.MarginModel,nativeMode.NativeTotalEquityUsd),inventory,prices,nativeMode);
    }
}
