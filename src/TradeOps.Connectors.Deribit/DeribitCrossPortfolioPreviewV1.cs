using System.Text.Json;
using TradeOps.Domain.Oms;

namespace TradeOps.Connectors.Deribit;

public sealed record DeribitCrossPortfolioTotalsV1(decimal EquityUsd,decimal MarginBalanceUsd,decimal InitialMarginUsd,
    decimal MaintenanceMarginUsd,decimal ProjectedInitialMarginEth,decimal CashEth,DateTimeOffset ObservedUtc);
public sealed record DeribitCrossPortfolioScenarioV1(decimal NativeUsdChange,DeribitCrossPortfolioTotalsV1 Totals);
/// <summary>Read-only hypothetical observations. Not a risk verdict, account READY or permission to send.</summary>
public sealed record DeribitCrossPortfolioPreviewV1(AccountKey Scope,DateTimeOffset StartedUtc,DateTimeOffset CompletedUtc,
    DeribitCrossPortfolioTotalsV1 Before,DeribitCrossPortfolioTotalsV1 After,IReadOnlyList<DeribitCrossPortfolioScenarioV1> Scenarios,
    IReadOnlyDictionary<string,decimal> IndexPrices)
{public int SchemaVersion=>1;public bool TradingReady=>false;public bool AccountReady=>false;}
public sealed class DeribitCrossPortfolioReadException(string stage,int? venueCode=null):InvalidOperationException("Native cross portfolio observation refused.")
{public string Stage{get;}=stage;public int? VenueCode{get;}=venueCode;}

public static class DeribitCrossPortfolioPreviewReaderV1
{
    public static async Task<DeribitCrossPortfolioPreviewV1> ReadAsync(DeribitPrivateCaptureBinding binding,HttpClient http,
        IReadOnlyList<decimal> changes,CancellationToken ct)
    {
        _=binding.Plan.Validate(binding.Options,binding.Scope);
        if(changes.Count is <1 or >7||changes.Distinct().Count()!=changes.Count||changes.Any(x=>x!=decimal.Truncate(x)||x is <-3 or >3)||!changes.Contains(0)||
            http.DefaultRequestHeaders.Authorization is not null||http.DefaultRequestHeaders.Contains("Cookie"))throw new ArgumentException("Bounded read-only preview required.");
        var started=DateTimeOffset.UtcNow;var phase="Authentication";var rpc=new DeribitRecoveryRpc(http,binding.Scope.Environment);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(30));var token=deadline.Token;
        try
        {
            var name="session:fix_cross_pm_preview_"+Guid.NewGuid().ToString("N");
            var auth=await rpc.Call("public/auth",new{grant_type="client_credentials",client_id=binding.Options.ClientId,client_secret=binding.Options.ClientSecret,
                scope=name+" expires:300 trade:read account:read wallet:none block_trade:none block_rfq:none"},token);
            var bearer=auth.GetProperty("access_token").GetString();
            if(!DeribitRecoveryQueries.ValidGrant(auth.GetProperty("scope").GetString()!,name)||auth.GetProperty("expires_in").GetInt32() is <120 or >300||
                string.IsNullOrWhiteSpace(bearer)||bearer.Length>8192||bearer.Any(char.IsControl))throw new InvalidOperationException();
            rpc.Token=bearer;phase="NativeAccount";var before=await Summary();
            phase="ContractUnits";var instrument=await rpc.Call("public/get_instrument",new{instrument_name="ETH-PERPETUAL"},token);
            if(Text(instrument,"instrument_name")!="ETH-PERPETUAL"||Text(instrument,"kind")!="future"||Text(instrument,"instrument_type")!="reversed"||
                Text(instrument,"base_currency")!="ETH"||Text(instrument,"settlement_currency")!="ETH"||Text(instrument,"quote_currency")!="USD"||
                Number(instrument,"contract_size")!=1||Number(instrument,"min_trade_amount")!=1||!instrument.GetProperty("is_active").GetBoolean())throw new InvalidOperationException();
            phase="NativeRiskMatrix";var pme=await rpc.Call("private/pme/simulate",new{currency="CROSS"},token);
            var rawIndices=pme.GetProperty("index_price");if(rawIndices.ValueKind!=JsonValueKind.Object||rawIndices.EnumerateObject().Count() is <1 or >128)throw new InvalidOperationException();
            var indices=new Dictionary<string,decimal>(StringComparer.Ordinal);
            foreach(var item in rawIndices.EnumerateObject())
            {if(item.Name.Length is <1 or >64||item.Name.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_')||!indices.TryAdd(item.Name,item.Value.GetDecimal())||indices[item.Name]<=0)throw new InvalidOperationException();}
            phase="NativeSimulation";var scenarios=new List<DeribitCrossPortfolioScenarioV1>();
            foreach(var change in changes.Order())
            {
                var simulated=await rpc.Call("private/simulate_portfolio",new{currency="ETH",add_positions=true,
                    simulated_positions=new Dictionary<string,decimal>{{"ETH-PERPETUAL",change}}},token);
                scenarios.Add(new(change,Parse(simulated)));
            }
            phase="NativeAccountRecheck";var after=await Summary();if(before.CashEth!=after.CashEth)throw new InvalidOperationException();
            return new(binding.Scope,started,DateTimeOffset.UtcNow,before,after,scenarios.AsReadOnly(),indices);
            async Task<DeribitCrossPortfolioTotalsV1> Summary()
            {
                var value=await rpc.Call("private/get_account_summary",new{currency="ETH",extended=true},token);
                if(value.GetProperty("id").GetInt64()!=binding.Plan.ExpectedAccountId||Text(value,"type")!=(binding.Plan.IsSubaccount?"subaccount":"main"))throw new InvalidOperationException();
                return Parse(value);
            }
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(DeribitRecoveryQueryException error){throw new DeribitCrossPortfolioReadException(phase,error.VenueCode);}
        catch(Exception error)when(error is OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or
            ArgumentException or FormatException or OverflowException or HttpRequestException or IOException){throw new DeribitCrossPortfolioReadException(phase);}
        finally{rpc.Token=null;}
    }
    private static DeribitCrossPortfolioTotalsV1 Parse(JsonElement value)
    {
        if(Text(value,"currency")!="ETH"||Text(value,"margin_model")!="cross_pm"||!value.GetProperty("cross_collateral_enabled").GetBoolean()||
            !value.GetProperty("portfolio_margining_enabled").GetBoolean())throw new InvalidOperationException();
        var totals=new DeribitCrossPortfolioTotalsV1(Number(value,"total_equity_usd"),Number(value,"total_margin_balance_usd"),Number(value,"total_initial_margin_usd"),
            Number(value,"total_maintenance_margin_usd"),Number(value,"projected_initial_margin"),Number(value,"balance"),DateTimeOffset.UtcNow);
        if(totals.InitialMarginUsd<0||totals.MaintenanceMarginUsd<0||totals.ProjectedInitialMarginEth<0||totals.MaintenanceMarginUsd>totals.InitialMarginUsd)throw new InvalidOperationException();
        return totals;
    }
    private static string Text(JsonElement value,string name)=>value.GetProperty(name).GetString()??throw new JsonException();
    private static decimal Number(JsonElement value,string name)=>value.GetProperty(name).GetDecimal();
}
