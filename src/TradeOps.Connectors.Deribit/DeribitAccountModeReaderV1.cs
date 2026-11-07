using TradeOps.Application.Oms;

namespace TradeOps.Connectors.Deribit;

/// <summary>Native account-mode observation only; no margin simulation, policy or execution authority.</summary>
public sealed record DeribitAccountModeObservationV1(string Currency,string MarginModel,bool CrossCollateral,bool PortfolioMargin,
    decimal CashBalance,decimal Equity,decimal AvailableFunds,decimal? NativeTotalEquityUsd,DateTimeOffset ObservedUtc);

public static class DeribitAccountModeReaderV1
{
    public static async Task<DeribitAccountModeObservationV1> ReadAsync(DeribitPrivateCaptureBinding binding,HttpClient http,CancellationToken ct)
    {
        _=binding.Plan.Validate(binding.Options,binding.Scope);
        if(http.DefaultRequestHeaders.Authorization is not null||http.DefaultRequestHeaders.Contains("Cookie"))throw new ArgumentException("Pinned read-only client required.");
        var rpc=new DeribitRecoveryRpc(http,binding.Scope.Environment);var name="session:fix_account_mode_"+Guid.NewGuid().ToString("N");
        try
        {
            var auth=await rpc.Call("public/auth",new{grant_type="client_credentials",client_id=binding.Options.ClientId,client_secret=binding.Options.ClientSecret,
                scope=name+" expires:300 trade:read account:read wallet:none block_trade:none block_rfq:none"},ct);
            var token=auth.GetProperty("access_token").GetString();
            if(!DeribitRecoveryQueries.ValidGrant(auth.GetProperty("scope").GetString()!,name)||auth.GetProperty("expires_in").GetInt32() is <120 or >300||
                string.IsNullOrWhiteSpace(token)||token.Length>8192||token.Any(char.IsControl))throw new InvalidOperationException("Account-mode read grant refused.");
            rpc.Token=token;
            var summary=await rpc.Call("private/get_account_summary",new{currency="ETH",extended=true},ct);
            if(summary.GetProperty("id").GetInt64()!=binding.Plan.ExpectedAccountId||summary.GetProperty("type").GetString()!=(binding.Plan.IsSubaccount?"subaccount":"main")||
                summary.GetProperty("currency").GetString()!="ETH")throw new InvalidOperationException("Native account-mode identity refused.");
            var mode=summary.GetProperty("margin_model").GetString();
            if(string.IsNullOrWhiteSpace(mode)||mode.Length>64||mode.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_'))throw new InvalidOperationException("Invalid native account mode.");
            decimal? total=summary.TryGetProperty("total_equity_usd",out var value)&&value.ValueKind!=System.Text.Json.JsonValueKind.Null?value.GetDecimal():null;
            return new("ETH",mode,summary.GetProperty("cross_collateral_enabled").GetBoolean(),summary.GetProperty("portfolio_margining_enabled").GetBoolean(),
                summary.GetProperty("balance").GetDecimal(),summary.GetProperty("equity").GetDecimal(),summary.GetProperty("available_funds").GetDecimal(),total,DateTimeOffset.UtcNow);
        }
        finally{rpc.Token=null;}
    }
}
