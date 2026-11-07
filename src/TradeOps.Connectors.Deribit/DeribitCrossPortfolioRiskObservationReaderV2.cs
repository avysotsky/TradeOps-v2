using System.Text.Json;
using TradeOps.Application.Oms;

namespace TradeOps.Connectors.Deribit;

/// <summary>Native cross-PM observations for bounded ETH inverse order entry. Read-only scope,
/// native USD simulations, full collateral inventory and settlement-currency debit traversal.
/// The caller still needs current live recovery and SQL/physical admission; this port issues no authority.</summary>
public sealed class DeribitCrossPortfolioRiskObservationReaderV2(DeribitPrivateCaptureBinding binding,HttpClient http,
    IOmsPendingRiskReader pending,TimeProvider? time=null):IDeribitCrossPortfolioRiskObservationReaderV2
{
    private static readonly string[] Settlements=["BTC","ETH","EURR","USDC","USDT"];
    public async Task<DeribitCrossPmRiskObservationV2?> ReadAsync(OmsJournalCommandRequest command,OmsRecoveredAccountStateV1 baseline,CancellationToken ct=default)
    {
        var clock=time??TimeProvider.System;var began=clock.GetUtcNow();
        if(command.Scope!=binding.Scope||baseline.Scope!=binding.Scope||baseline.Epoch==Guid.Empty||command.Payload.Instrument!="ETH-PERPETUAL"||
            command.Payload.Quantity!=1||command.Payload.LimitPrice is not>0||http.DefaultRequestHeaders.Authorization is not null||http.DefaultRequestHeaders.Contains("Cookie"))return null;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(5));var token=deadline.Token;
        using var budget=new DeribitRecoveryReadBudgetV2();var rpc=new DeribitRecoveryRpc(http,binding.Scope.Environment,budget);
        try
        {
            _=binding.Plan.Validate(binding.Options,binding.Scope);
            var outstanding=await pending.ReadAsync(command.Scope,command.CommandId,token);
            if(outstanding.Count>2||outstanding.Any(x=>x.Instrument!="ETH-PERPETUAL"||x.NativeUsd!=1||x.LimitPrice<=0))return null;
            var open=baseline.Orders.Where(x=>x.State.NativeState=="open").ToArray();
            if(open.Any(x=>x.State.NativeInstrument!="ETH-PERPETUAL"||x.State.NativeAmount!=1||x.State.KnownFilledNativeAmount!=0||x.LastReport.Price is not>0))return null;
            var radius=1+outstanding.Count+open.Length;if(radius>3)return null;
            var names=baseline.Positions.Select(x=>x.NativeInstrument).Append("ETH-PERPETUAL").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if(names.Length>16)return null;
            var session="session:cross_pm_risk_"+Guid.NewGuid().ToString("N");
            var auth=await rpc.Call("public/auth",new{grant_type="client_credentials",client_id=binding.Options.ClientId,client_secret=binding.Options.ClientSecret,
                scope=session+" expires:300 trade:read account:read wallet:none block_trade:none block_rfq:none"},token);
            var bearer=Text(auth,"access_token");
            if(!DeribitRecoveryQueries.ValidGrant(Text(auth,"scope"),session)||auth.GetProperty("expires_in").GetInt32() is <120 or >300||bearer.Length is <1 or >8192||bearer.Any(char.IsControl))return null;
            rpc.Token=bearer;
            var before=await Summary(); // Verify the pinned native ID/type before any portfolio reads.
            if(before.MarginModel!="cross_pm"||!before.CrossCollateral||!before.PortfolioMargin)return null;
            var beforeAll=await AllBalances();
            var nativeThrough=DateTimeOffset.FromUnixTimeMilliseconds((await rpc.Call("public/get_time",new{},token)).GetInt64());
            if(nativeThrough<began.AddSeconds(-2)||nativeThrough>clock.GetUtcNow().AddSeconds(2))return null;
            var from=new DateTimeOffset(nativeThrough.UtcDateTime.Date,TimeSpan.Zero);
            var markets=new List<DeribitCrossPmInstrumentV2>();var scenarios=new List<DeribitCrossPmScenarioV2>();var debits=new List<DeribitCrossPmDebitV2>();
            var work=new List<Task>();
            foreach(var name in names)work.Add(Market(name));
            foreach(var change in Enumerable.Range(-radius,2*radius+1))work.Add(Scenario(change));
            foreach(var currency in Settlements)work.Add(Loss(currency));
            JsonElement pme=default,fees=default;
            work.Add(ReadPme());work.Add(ReadFees());
            // Drain every child before disposing its shared byte/rate/concurrency budget, including on failure.
            await Task.WhenAll(work);
            var afterAll=await AllBalances();var after=await Summary();
            if(!SameCash(beforeAll.Balances,afterAll.Balances))return null;
            var indices=pme.GetProperty("index_price");if(indices.ValueKind!=JsonValueKind.Object||indices.EnumerateObject().Count() is <1 or >128)return null;
            var fx=new Dictionary<string,decimal>(StringComparer.Ordinal);var unique=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in indices.EnumerateObject())
            {
                if(!unique.Add(property.Name)||property.Name.Length is <1 or >64||property.Name.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='_'))return null;
                var value=property.Value.GetDecimal();if(value<=0)return null;
                foreach(var currency in Settlements)if(property.Name==currency.ToLowerInvariant()+"_usd")fx.Add(currency,value);
            }
            var loss=debits.OrderBy(x=>x.Currency,StringComparer.Ordinal).Select(x=>x with{NegativeSessionPnlBound=Math.Max(
                beforeAll.SessionLoss.GetValueOrDefault(x.Currency),afterAll.SessionLoss.GetValueOrDefault(x.Currency))}).ToArray();
            var done=clock.GetUtcNow();if(done<began||done-began>TimeSpan.FromSeconds(5))return null;
            return new(2,binding.Scope,baseline.Epoch,began,done,from,nativeThrough,markets.OrderBy(x=>x.Terms.Name,StringComparer.Ordinal).ToArray(),
                afterAll.Balances,before,after,scenarios.OrderBy(x=>x.NativeUsdChange).ToArray(),outstanding,loss,fx,
                new[]{Number(fees,"buy_taker_fee"),Number(fees,"sell_taker_fee"),Number(fees,"buy_maker_fee"),Number(fees,"sell_maker_fee"),0}.Max(),Number(fees,"min_price"),Number(fees,"max_price"));

            async Task<DeribitCrossPmCapitalV2> Summary()
            {
                var row=await rpc.Call("private/get_account_summary",new{currency="ETH",extended=true},token);Identity(row);
                return Capital(row);
            }
            async Task<(IReadOnlyList<DeribitCurrencyBalance> Balances,Dictionary<string,decimal> SessionLoss)> AllBalances()
            {
                var requested=clock.GetUtcNow();var result=await rpc.Call("private/get_account_summaries",new{extended=true},token);var received=clock.GetUtcNow();Identity(result);
                var rows=result.GetProperty("summaries");if(rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength() is <1 or >32)throw new JsonException();
                var values=new List<DeribitCurrencyBalance>();var sessions=new Dictionary<string,decimal>(StringComparer.Ordinal);
                foreach(var row in rows.EnumerateArray())
                {
                    var currency=Text(row,"currency");if(currency.Length is <1 or >32||currency.Any(c=>!char.IsAsciiLetterUpper(c)&&!char.IsAsciiDigit(c)&&c!='_')||sessions.ContainsKey(currency))throw new JsonException();
                    values.Add(new(currency,Number(row,"balance"),Number(row,"equity"),row.GetProperty("cross_collateral_enabled").GetBoolean(),row.GetProperty("portfolio_margining_enabled").GetBoolean(),new(requested,received)));
                    // Native PnL is in the summary's currency; profit never offsets a debit bound.
                    sessions.Add(currency,Math.Max(0,-Number(row,"session_rpl"))+Math.Max(0,-Number(row,"session_upl")));
                    if(!Settlements.Contains(currency,StringComparer.Ordinal)&&(Number(row,"balance")!=0||Number(row,"equity")!=0||sessions[currency]!=0))throw new InvalidOperationException("Unsupported active collateral.");
                }
                return(values,sessions);
            }
            async Task Market(string name)
            {
                var row=await rpc.Call("public/get_instrument",new{instrument_name=name},token);var ticker=await rpc.Call("public/ticker",new{instrument_name=name},token);
                if(Text(row,"instrument_name")!=name||Text(ticker,"instrument_name")!=name||Math.Abs(ticker.GetProperty("timestamp").GetInt64()-clock.GetUtcNow().ToUnixTimeMilliseconds())>2000)throw new JsonException();
                var terms=new DeribitRiskInstrumentV1(name,Text(row,"kind"),Text(row,"instrument_type"),Text(row,"base_currency"),Text(row,"quote_currency"),Text(row,"settlement_currency"),row.GetProperty("is_active").GetBoolean(),
                    DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("expiration_timestamp").GetInt64()),Number(row,"min_trade_amount"),Number(row,"tick_size"),
                    row.GetProperty("tick_size_steps").EnumerateArray().Select(x=>new DeribitRiskPriceStepV1(Number(x,"above_price"),Number(x,"tick_size"))).ToArray(),Number(ticker,"mark_price"),Number(ticker,"index_price"));
                var observed=new DeribitCrossPmInstrumentV2(terms,Number(row,"contract_size"),Number(ticker,"best_bid_price"),Number(ticker,"best_ask_price"),clock.GetUtcNow());
                lock(markets)markets.Add(observed);
            }
            async Task Scenario(int change)
            {
                var row=await rpc.Call("private/simulate_portfolio",new{currency="ETH",add_positions=true,simulated_positions=new Dictionary<string,int>{{"ETH-PERPETUAL",change}}},token);
                var value=new DeribitCrossPmScenarioV2(change,Capital(row));lock(scenarios)scenarios.Add(value);
            }
            async Task Loss(string currency)
            {
                long? continuation=null;var cursors=new HashSet<long>();var rows=new Dictionary<long,JsonElement>();decimal total=0;
                for(var page=0;;page++)
                {
                    if(page>=4)throw new InvalidOperationException("Incomplete debit traversal.");
                    var args=new Dictionary<string,object>{{"currency",currency},{"start_timestamp",from.ToUnixTimeMilliseconds()},{"end_timestamp",nativeThrough.ToUnixTimeMilliseconds()},{"count",1000}};
                    if(continuation.HasValue)args.Add("continuation",continuation.Value);
                    var result=await rpc.Call("private/get_transaction_log",args,token);var logs=result.GetProperty("logs");
                    if(logs.ValueKind!=JsonValueKind.Array||logs.GetArrayLength()>1000)throw new JsonException();
                    foreach(var row in logs.EnumerateArray())
                    {
                        var at=row.GetProperty("timestamp").GetInt64();if(Text(row,"currency")!=currency||row.GetProperty("user_id").GetInt64()!=binding.Plan.ExpectedAccountId||at<from.ToUnixTimeMilliseconds()||at>nativeThrough.ToUnixTimeMilliseconds())throw new JsonException();
                        var id=row.GetProperty("id").GetInt64();if(id<0)throw new JsonException();
                        if(rows.TryGetValue(id,out var prior)){if(!JsonElement.DeepEquals(prior,row))throw new JsonException();continue;}
                        rows.Add(id,row.Clone());total+=Math.Max(0,-Nullable(row,"change"))+Math.Max(0,-Nullable(row,"cashflow"))+Math.Abs(Nullable(row,"commission"));
                    }
                    var next=result.GetProperty("continuation");if(next.ValueKind==JsonValueKind.Null)break;
                    continuation=next.GetInt64();if(continuation<0||!cursors.Add(continuation.Value)||logs.GetArrayLength()==0)throw new JsonException();
                }
                lock(debits)debits.Add(new(currency,total,0));
            }
            async Task ReadPme()=>pme=await rpc.Call("private/pme/simulate",new{currency="CROSS"},token);
            async Task ReadFees()
            {
                var price=outstanding.Select(x=>x.LimitPrice).Concat(open.Select(x=>x.LastReport.Price!.Value)).Append(command.Payload.LimitPrice!.Value).Min();
                fees=await rpc.Call("private/get_margins",new{instrument_name="ETH-PERPETUAL",amount=radius,price},token);
            }
            void Identity(JsonElement row)
            {if(row.GetProperty("id").GetInt64()!=binding.Plan.ExpectedAccountId||Text(row,"type")!=(binding.Plan.IsSubaccount?"subaccount":"main"))throw new InvalidOperationException("Native account mismatch.");}
            DeribitCrossPmCapitalV2 Capital(JsonElement row)=>new(Text(row,"currency"),Text(row,"margin_model"),row.GetProperty("cross_collateral_enabled").GetBoolean(),row.GetProperty("portfolio_margining_enabled").GetBoolean(),
                Number(row,"total_equity_usd"),Number(row,"total_margin_balance_usd"),Number(row,"total_initial_margin_usd"),Number(row,"total_maintenance_margin_usd"),Number(row,"projected_initial_margin"),clock.GetUtcNow());
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch(Exception e)when(e is OperationCanceledException or HttpRequestException or IOException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException or DeribitRecoveryQueryException){return null;}
        finally{rpc.Token=null;}
    }
    private static bool SameCash(IReadOnlyList<DeribitCurrencyBalance> a,IReadOnlyList<DeribitCurrencyBalance> b)=>
        a.Select(x=>(x.Currency,x.CashBalance,x.CrossCollateralEnabled,x.PortfolioMarginingEnabled)).OrderBy(x=>x.Currency,StringComparer.Ordinal)
        .SequenceEqual(b.Select(x=>(x.Currency,x.CashBalance,x.CrossCollateralEnabled,x.PortfolioMarginingEnabled)).OrderBy(x=>x.Currency,StringComparer.Ordinal));
    private static string Text(JsonElement row,string name)=>row.GetProperty(name).GetString()??throw new JsonException();
    private static decimal Number(JsonElement row,string name)=>row.GetProperty(name).GetDecimal();
    private static decimal Nullable(JsonElement row,string name)=>row.GetProperty(name).ValueKind==JsonValueKind.Null?0:Number(row,name);
}
