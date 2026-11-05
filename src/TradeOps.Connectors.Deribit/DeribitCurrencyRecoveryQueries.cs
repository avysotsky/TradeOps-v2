using System.Text.Json;
using TradeOps.Application.Oms;

namespace TradeOps.Connectors.Deribit;

/// <summary>Read-only native enumeration across symbols. Metadata remains server-owned. An unknown
/// instrument blocks the whole batch instead of being discarded, inferred from its name, or given default units.</summary>
public sealed class DeribitCurrencyRecoveryQueries(DeribitPrivateCaptureBinding binding, HttpClient http) : IDeribitCurrencyRecoveryQueries
{
    public async Task<DeribitCurrencyRecoveryResult> CollectAsync(DeribitCurrencyRecoveryQuery q, CancellationToken ct = default)
    {
        _ = binding.Plan.Validate(binding.Options, binding.Scope);
        if (q.Scope != binding.Scope || q.Epoch == Guid.Empty || q.Currency is not ("BTC" or "ETH" or "USDC" or "USDT" or "EURR") || q.Kind is not ("future" or "option" or "any") ||
            q.FromUtc.Offset != TimeSpan.Zero || q.ThroughUtc.Offset != TimeSpan.Zero || q.FromUtc <= DateTimeOffset.UnixEpoch.AddHours(1) ||
            q.ThroughUtc < q.FromUtc || q.ThroughUtc > DateTimeOffset.UtcNow || q.ThroughUtc-q.FromUtc > TimeSpan.FromDays(7) ||
            q.Overlap < TimeSpan.FromMilliseconds(1) || q.Overlap > TimeSpan.FromHours(1) || q.PageSize is < 2 or > 1000 ||
            q.MaximumPagesPerSource is < 1 or > 20 || q.MaximumFacts is < 1 or > 10000 ||
            q.Kind != "any" && !binding.Plan.Instruments.Values.Any(x => x.Currency == q.Currency && x.Kind == q.Kind) ||
            http.DefaultRequestHeaders.Authorization is not null || http.DefaultRequestHeaders.Contains("Cookie"))
            throw new ArgumentException("Invalid currency recovery query/binding.");
        var started = DateTimeOffset.UtcNow;
        var from = DateTimeOffset.FromUnixTimeMilliseconds((q.FromUtc-q.Overlap).ToUnixTimeMilliseconds());
        var through = DateTimeOffset.FromUnixTimeMilliseconds(q.ThroughUtc.ToUnixTimeMilliseconds());
        var sources = new List<DeribitRecoverySourceRead>(); var facts = new List<OmsPrivateEventEnvelope>();
        var hashes = new Dictionary<(string, string), byte[]>(); var problems = new List<DeribitCurrencyRecoveryProblem>();
        var rpc = new DeribitRecoveryRpc(http, q.Scope.Environment);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        try
        {
            var session = "session:currency_recovery_" + Guid.NewGuid().ToString("N");
            var auth = await rpc.Call("public/auth", new { grant_type = "client_credentials", client_id = binding.Options.ClientId,
                client_secret = binding.Options.ClientSecret,
                scope = session + " expires:300 trade:read account:read wallet:none block_trade:none block_rfq:none" }, token);
            var bearer = auth.GetProperty("access_token").GetString();
            if (!DeribitRecoveryQueries.ValidGrant(auth.GetProperty("scope").GetString()!, session) ||
                auth.GetProperty("expires_in").GetInt32() is < 120 or > 300 || string.IsNullOrWhiteSpace(bearer) ||
                bearer.Length > 8192 || bearer.Any(char.IsControl)) throw new Refused(DeribitCurrencyRecoveryProblem.BindingInvalid);
            rpc.Token = bearer;
            await Verify();
            await Read("open", false, false);
            await Read("orders-recent", false, true); await Read("orders-historical", true, true);
            await Read("trades-recent", false, false); await Read("trades-historical", true, false);
            await Verify();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Refused error) { problems.Add(error.Problem); }
        catch (DeribitRecoveryQueryException error) { problems.Add(error.Problem == DeribitRecoveryQueryProblem.Capacity
            ? DeribitCurrencyRecoveryProblem.Capacity : DeribitCurrencyRecoveryProblem.ReadFailed); }
        catch (Exception error) when (error is OperationCanceledException or JsonException or HttpRequestException or IOException or
            InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException)
        { problems.Add(DeribitCurrencyRecoveryProblem.ReadFailed); }
        finally { rpc.Token = null; }
        if (problems.Count != 0) facts.Clear();
        return new(q.Scope, q.Epoch, q.Currency, q.Kind, from, through, started, DateTimeOffset.UtcNow,
            sources.AsReadOnly(), facts.AsReadOnly(), problems.AsReadOnly());

        async Task Verify()
        {
            var summary = await rpc.Call("private/get_account_summary", new { currency = q.Currency, extended = true }, token);
            if (summary.GetProperty("id").GetInt64() != binding.Plan.ExpectedAccountId ||
                summary.GetProperty("type").GetString() != (binding.Plan.IsSubaccount ? "subaccount" : "main") ||
                summary.GetProperty("currency").GetString() != q.Currency) throw new Refused(DeribitCurrencyRecoveryProblem.BindingInvalid);
        }

        async Task Read(string name, bool historical, bool orders)
        {
            var open = name == "open"; var execution = !open && !orders;
            var method = open ? "private/get_open_orders_by_currency" : orders
                ? "private/get_order_history_by_currency" : "private/get_user_trades_by_currency";
            var index = sources.Count; sources.Add(new(name, 0, false));
            var pages = new HashSet<string>(StringComparer.Ordinal); var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null; DateTimeOffset? lastTradeTime = null;
            for (var page = 1; page <= q.MaximumPagesPerSource; page++)
            {
                var p = new Dictionary<string, object> { ["currency"] = q.Currency, ["kind"] = q.Kind };
                if (!open)
                {
                    p["historical"] = historical; p["count"] = q.PageSize;
                    if (orders) { p["include_old"] = true; p["include_unfilled"] = !historical; p["offset"] = (page-1)*q.PageSize; }
                    else
                    {
                        p["sorting"] = "asc"; p["end_timestamp"] = through.ToUnixTimeMilliseconds();
                        // Currency traversal uses opaque trade IDs; trade_seq belongs to an instrument.
                        if (cursor is null) p["start_timestamp"] = from.ToUnixTimeMilliseconds(); else p["start_id"] = cursor;
                    }
                }
                var raw = await rpc.Call(method, p, token); var received = DateTimeOffset.UtcNow;
                var rows = execution ? raw.GetProperty("trades") : raw;
                if (rows.ValueKind != JsonValueKind.Array) throw new FormatException();
                if (rows.GetArrayLength() > (open ? q.MaximumFacts : q.PageSize)) throw new Refused(DeribitCurrencyRecoveryProblem.Capacity);
                sources[index] = new(name, page, false); var keys = new List<string>(); string? lastId = null;
                foreach (var item in rows.EnumerateArray())
                {
                    var symbol = item.GetProperty("instrument_name").GetString()!;
                    if (!binding.Plan.Instruments.TryGetValue(symbol, out var metadata)) throw new Refused(DeribitCurrencyRecoveryProblem.UnmappedInstrument);
                    if (metadata.Currency != q.Currency || q.Kind != "any" && metadata.Kind != q.Kind) throw new Refused(DeribitCurrencyRecoveryProblem.BindingInvalid);
                    var native = DeribitWebSocketMessageParser.ParsePrivateFacts(item, metadata, execution); native.Validate();
                    if (native.VenueTimestampUtc > received || open && native.OrderState is not ("open" or "triggered" or "untriggered")) throw new FormatException();
                    if (execution)
                    {
                        if (native.Fee is null || native.FeeCurrency is null || native.VenueTimestampUtc < from || native.VenueTimestampUtc > through ||
                            lastTradeTime.HasValue && native.VenueTimestampUtc < lastTradeTime) throw new Refused(DeribitCurrencyRecoveryProblem.Pagination);
                        lastTradeTime = native.VenueTimestampUtc; lastId = native.ExchangeTradeId;
                    }
                    var envelope = new OmsPrivateEventEnvelope(q.Scope, native, new(Guid.NewGuid(), q.Epoch, received,
                        OmsPrivateCaptureSource.RecoveryQuery, method)); envelope.Validate();
                    var canonical = OmsPrivateEventCanonical.Encode(envelope); keys.Add(canonical.EventId + ":" + Convert.ToHexString(canonical.Sha256));
                    if (!open && !execution && (native.VenueTimestampUtc < from || native.VenueTimestampUtc > through)) continue;
                    var key = (canonical.Namespace, canonical.EventId);
                    if (hashes.TryGetValue(key, out var prior))
                    { if (!prior.SequenceEqual(canonical.Sha256)) throw new Refused(DeribitCurrencyRecoveryProblem.FactCollision); continue; }
                    if (facts.Count == q.MaximumFacts) throw new Refused(DeribitCurrencyRecoveryProblem.Capacity);
                    hashes.Add(key, canonical.Sha256); facts.Add(envelope);
                }
                var more = !open && (orders ? rows.GetArrayLength() == q.PageSize : raw.GetProperty("has_more").GetBoolean());
                if (!more) { sources[index] = new(name, page, true); return; }
                if (keys.Count == 0 || !pages.Add(string.Join('|', keys.Order(StringComparer.Ordinal)))) throw new Refused(DeribitCurrencyRecoveryProblem.Pagination);
                if (execution)
                {
                    // Inclusive boundary repeats deduplicate above. No timestamp+1 or numeric ID increment can lose same-ms fills.
                    if (lastId is null || !cursors.Add(lastId)) throw new Refused(DeribitCurrencyRecoveryProblem.Pagination);
                    cursor = lastId;
                }
            }
            throw new Refused(DeribitCurrencyRecoveryProblem.Capacity);
        }
    }
    private sealed class Refused(DeribitCurrencyRecoveryProblem problem) : Exception
    { internal DeribitCurrencyRecoveryProblem Problem { get; } = problem; }
}
