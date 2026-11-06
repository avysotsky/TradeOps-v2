using System.Text.Json;
using TradeOps.Application.Oms;

namespace TradeOps.Connectors.Deribit;

/// <summary>Read-only, bounded native risk inputs. Daily debit metric deliberately counts negative
/// cashflow, negative balance change and fees without profit offsets (possible conservative duplication).</summary>
public sealed class DeribitRiskObservationReader(DeribitPrivateCaptureBinding binding, HttpClient http,
    TimeProvider? time = null,IOmsPendingRiskReader? pendingRisk=null) : IDeribitRiskObservationReader
{
    public async Task<DeribitRiskObservationV1?> ReadAsync(OmsJournalCommandRequest command, OmsRecoveredAccountStateV1 baseline, CancellationToken ct = default)
    {
        if (command.Scope != binding.Scope || baseline.Scope != binding.Scope || baseline.Epoch == Guid.Empty ||
            http.DefaultRequestHeaders.Authorization is not null || http.DefaultRequestHeaders.Contains("Cookie")) return null;
        var clock = time ?? TimeProvider.System; var began = clock.GetUtcNow();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var rpc = new DeribitRecoveryRpc(http, binding.Scope.Environment);
        try
        {
            if(pendingRisk is null)return null;
            var pending=await pendingRisk.ReadAsync(command.Scope,command.CommandId,deadline.Token);
            var quantity=new Dictionary<string,(decimal Amount,decimal Price)>(StringComparer.Ordinal);
            void Add(string name,decimal amount,decimal price)
            {
                if(amount<0||price<=0)throw new InvalidOperationException("Unknown pending native terms.");
                var prior=quantity.GetValueOrDefault(name,(0,price));quantity[name]=(prior.Amount+amount,Math.Min(prior.Price,price));
            }
            foreach(var row in pending)Add(row.Instrument,row.NativeUsd,row.LimitPrice);
            foreach(var row in baseline.Orders.Where(x=>x.State.NativeState=="open"))
            {
                if(row.State.AmountUnit!=OmsPrivateAmountUnit.Usd||row.State.AmountCurrency!="USD"||row.State.KnownFilledNativeAmount is not{} filled||
                    row.LastReport.PriceKind!=OmsPrivatePriceKind.Numeric||row.LastReport.Price is not>0) return null;
                Add(row.State.NativeInstrument,row.State.NativeAmount-filled,row.LastReport.Price.Value);
            }
            Add(command.Payload.Instrument,command.Payload.Quantity,command.Payload.LimitPrice??0);
            _ = binding.Plan.Validate(binding.Options, binding.Scope);
            var currencies = baseline.Balances.Select(x => x.Currency).Order(StringComparer.Ordinal).ToArray();
            if (currencies.Length is < 1 or > 2 || currencies.Any(x => x is not ("BTC" or "ETH"))) return null;
            var names = baseline.Positions.Select(x => x.NativeInstrument).Concat(baseline.Orders.Where(x => x.State.NativeState == "open").Select(x => x.State.NativeInstrument))
                .Concat(quantity.Keys).Concat(currencies.Select(x => x + "-PERPETUAL")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (names.Length > 256) return null;
            var session = "session:risk_" + Guid.NewGuid().ToString("N");
            var auth = await rpc.Call("public/auth", new { grant_type = "client_credentials", client_id = binding.Options.ClientId,
                client_secret = binding.Options.ClientSecret, scope = session + " expires:300 trade:read account:read wallet:none block_trade:none block_rfq:none" }, deadline.Token);
            if (!DeribitRecoveryQueries.ValidGrant(auth.GetProperty("scope").GetString()!, session) || auth.GetProperty("expires_in").GetInt32() is < 120 or > 300) return null;
            var bearer = auth.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(bearer) || bearer.Length > 8192 || bearer.Any(char.IsControl)) return null;
            rpc.Token = bearer;
            var through = DateTimeOffset.FromUnixTimeMilliseconds((await rpc.Call("public/get_time", new { }, deadline.Token)).GetInt64());
            if (through < began.AddSeconds(-2) || through > clock.GetUtcNow().AddSeconds(2)) return null;
            var from = new DateTimeOffset(through.UtcDateTime.Date, TimeSpan.Zero);
            var markets = new List<DeribitRiskInstrumentV1>();
            foreach (var name in names)
            {
                var m = await rpc.Call("public/get_instrument", new { instrument_name = name }, deadline.Token);
                var ticker = await rpc.Call("public/ticker", new { instrument_name = name }, deadline.Token);
                if (m.GetProperty("instrument_name").GetString() != name || ticker.GetProperty("instrument_name").GetString() != name ||
                    Math.Abs(ticker.GetProperty("timestamp").GetInt64() - clock.GetUtcNow().ToUnixTimeMilliseconds()) > 2000) return null;
                markets.Add(new(name, Text(m,"kind"), Text(m,"instrument_type"), Text(m,"base_currency"), Text(m,"quote_currency"), Text(m,"settlement_currency"),
                    m.GetProperty("is_active").GetBoolean(), DateTimeOffset.FromUnixTimeMilliseconds(m.GetProperty("expiration_timestamp").GetInt64()),
                    Num(m,"min_trade_amount"), Num(m,"tick_size"), m.GetProperty("tick_size_steps").EnumerateArray()
                        .Select(x => new DeribitRiskPriceStepV1(Num(x,"above_price"), Num(x,"tick_size"))).ToArray(), Num(ticker,"mark_price"), Num(ticker,"index_price")));
            }
            var metrics = new List<DeribitRiskCurrencyV1>();
            foreach (var currency in currencies)
            {
                var summary = await Summary(currency); var logs = new Dictionary<long, JsonElement>(); long? continuation = null;
                var cursors = new HashSet<long>(); decimal loss = 0;
                for (var page = 0; ; page++)
                {
                    if (page >= 4) return null;
                    var args = new Dictionary<string, object> { ["currency"] = currency, ["start_timestamp"] = from.ToUnixTimeMilliseconds(),
                        ["end_timestamp"] = through.ToUnixTimeMilliseconds(), ["count"] = 1000 };
                    if (continuation.HasValue) args["continuation"] = continuation.Value;
                    var result = await rpc.Call("private/get_transaction_log", args, deadline.Token);
                    var rows = result.GetProperty("logs"); if (rows.GetArrayLength() > 1000) return null;
                    foreach (var row in rows.EnumerateArray())
                    {
                        if (Text(row,"currency") != currency || row.GetProperty("user_id").GetInt64() != binding.Plan.ExpectedAccountId ||
                            row.GetProperty("timestamp").GetInt64() < from.ToUnixTimeMilliseconds() || row.GetProperty("timestamp").GetInt64() > through.ToUnixTimeMilliseconds()) return null;
                        var id = row.GetProperty("id").GetInt64();
                        if (logs.TryGetValue(id, out var prior)) { if (!JsonElement.DeepEquals(prior, row)) return null; continue; }
                        logs.Add(id, row.Clone());
                        loss += Math.Max(0, -NullableNum(row,"change")) + Math.Max(0, -NullableNum(row,"cashflow")) + Math.Abs(NullableNum(row,"commission"));
                    }
                    var next = result.GetProperty("continuation"); if (next.ValueKind == JsonValueKind.Null) break;
                    continuation = next.GetInt64(); if (continuation < 0 || !cursors.Add(continuation.Value) || rows.GetArrayLength() == 0) return null;
                }
                var after = await Summary(currency);
                if (Num(summary,"balance") != Num(after,"balance") || summary.GetProperty("cross_collateral_enabled").GetBoolean() != after.GetProperty("cross_collateral_enabled").GetBoolean() ||
                    summary.GetProperty("portfolio_margining_enabled").GetBoolean() != after.GetProperty("portfolio_margining_enabled").GetBoolean()) return null;
                var fx = markets.Single(x => x.Name == currency + "-PERPETUAL").Index;
                metrics.Add(new(currency, Num(after,"balance"), Num(after,"equity"),Num(after,"margin_balance"),Num(after,"available_funds"), after.GetProperty("cross_collateral_enabled").GetBoolean(),
                    after.GetProperty("portfolio_margining_enabled").GetBoolean(), Num(after,"initial_margin"), Num(after,"projected_initial_margin"),
                    Num(after,"session_rpl"), Num(after,"session_upl"), loss, fx));
            }
            var bounds=new List<DeribitRiskMarginBoundV1>();JsonElement margins=default;
            foreach(var (name,volume) in quantity.OrderBy(x=>x.Key,StringComparer.Ordinal))
            {
                if(volume.Amount==0)continue;
                var m=await rpc.Call("private/get_margins",new{instrument_name=name,amount=volume.Amount,price=volume.Price},deadline.Token);
                var currency=markets.Single(x=>x.Name==name).SettlementCurrency;
                bounds.Add(new(name,currency,volume.Amount,Num(m,"buy"),Num(m,"sell"),new[]{Num(m,"buy_taker_fee"),Num(m,"sell_taker_fee"),Num(m,"buy_maker_fee"),Num(m,"sell_maker_fee"),0}.Max()));
                if(name==command.Payload.Instrument)margins=m;
            }
            return new(binding.Scope, baseline.Epoch, began, clock.GetUtcNow(), from, through, markets.ToArray(), metrics.ToArray(),
                Num(margins,"buy"), Num(margins,"sell"), new[] { Num(margins,"buy_taker_fee"), Num(margins,"sell_taker_fee"),
                    Num(margins,"buy_maker_fee"), Num(margins,"sell_maker_fee"), 0 }.Max(), Num(margins,"min_price"), Num(margins,"max_price"),bounds.ToArray());
            async Task<JsonElement> Summary(string currency)
            {
                var s = await rpc.Call("private/get_account_summary", new { currency, extended = true }, deadline.Token);
                if (s.GetProperty("id").GetInt64() != binding.Plan.ExpectedAccountId || Text(s,"type") != (binding.Plan.IsSubaccount ? "subaccount" : "main") || Text(s,"currency") != currency||Text(s,"margin_model")!="segregated_sm")
                    throw new InvalidOperationException("Native account mismatch.");
                return s;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException or JsonException or
            InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException or DeribitRecoveryQueryException) { return null; }
        finally { rpc.Token = null; }
    }
    private static string Text(JsonElement x, string key) => x.GetProperty(key).GetString() ?? throw new JsonException();
    private static decimal Num(JsonElement x, string key) => x.GetProperty(key).GetDecimal();
    private static decimal NullableNum(JsonElement x, string key) => x.GetProperty(key).ValueKind == JsonValueKind.Null ? 0 : Num(x,key);
}
