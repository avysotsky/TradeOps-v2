using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Connectors.Deribit;

/// <summary>Bounded per-instrument read queries. Requires a redirect/cookie/retry-free HttpClient.</summary>
public sealed class DeribitRecoveryQueries(DeribitPrivateCaptureBinding binding, HttpClient http) : IDeribitRecoveryQueries
{
    public async Task<DeribitRecoveryQueryResult> CollectAsync(DeribitRecoveryQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        _ = binding.Plan.Validate(binding.Options, binding.Scope);
        if (query.Scope != binding.Scope || query.Epoch == Guid.Empty ||
            !binding.Plan.Instruments.TryGetValue(query.NativeInstrument, out var metadata) ||
            query.FromUtc.Offset != TimeSpan.Zero || query.ThroughUtc.Offset != TimeSpan.Zero ||
            query.FromUtc <= DateTimeOffset.UnixEpoch + TimeSpan.FromHours(1) || query.FromUtc > query.ThroughUtc ||
            query.ThroughUtc > DateTimeOffset.UtcNow || query.ThroughUtc - query.FromUtc > TimeSpan.FromDays(7) ||
            query.Overlap < TimeSpan.FromMilliseconds(1) || query.Overlap > TimeSpan.FromHours(1) ||
            query.PageSize is < 1 or > 1000 || query.MaximumPagesPerSource is < 1 or > 20 ||
            query.MaximumFacts is < 1 or > 10000 || http.DefaultRequestHeaders.Authorization is not null ||
            http.DefaultRequestHeaders.Contains("Cookie")) throw new ArgumentException("Invalid bounded recovery query/binding.");
        var started = DateTimeOffset.UtcNow;
        // Venue timestamps have millisecond resolution; round outward at the lower bound.
        var from = DateTimeOffset.FromUnixTimeMilliseconds((query.FromUtc - query.Overlap).ToUnixTimeMilliseconds());
        var through = DateTimeOffset.FromUnixTimeMilliseconds(query.ThroughUtc.ToUnixTimeMilliseconds());
        var sources = new List<DeribitRecoverySourceRead>();
        var observations = new List<OmsPrivateEventEnvelope>();
        var distinct = new Dictionary<(string, string), byte[]>();
        var problems = new List<DeribitRecoveryQueryProblem>();
        var rpc = new DeribitRecoveryRpc(http, query.Scope.Environment);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        try
        {
            var session = "session:recovery_" + Guid.NewGuid().ToString("N");
            var auth = await rpc.Call("public/auth", new { grant_type = "client_credentials", client_id = binding.Options.ClientId,
                client_secret = binding.Options.ClientSecret, scope = session + " expires:300 trade:read account:read wallet:none block_trade:none block_rfq:none" }, token);
            if (!ValidGrant(auth.GetProperty("scope").GetString()!, session) || auth.GetProperty("expires_in").GetInt32() is < 120 or > 300)
                throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.Authentication);
            var accessToken = auth.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 8192 || accessToken.Any(char.IsControl))
                throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.Authentication);
            rpc.Token = accessToken;
            await VerifyAccount();
            await ReadSource("open", false, false);
            await ReadSource("orders-recent", false, true);
            await ReadSource("orders-historical", true, true);
            await ReadSource("trades-recent", false, false);
            await ReadSource("trades-historical", true, false);
            await VerifyAccount();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { problems.Add(DeribitRecoveryQueryProblem.Deadline); }
        catch (DeribitRecoveryQueryException e) { problems.Add(e.Problem); }
        catch (Exception e) when (e is HttpRequestException or IOException) { problems.Add(DeribitRecoveryQueryProblem.Transport); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException)
        { problems.Add(DeribitRecoveryQueryProblem.Protocol); }
        finally { rpc.Token = null; }
        if (problems.Count != 0) observations.Clear(); // Never expose partial evidence as a valid recovery batch.
        return new(query.Scope, query.Epoch, metadata.Name, metadata.Currency, from, through, started, DateTimeOffset.UtcNow,
            sources.AsReadOnly(), observations.AsReadOnly(), problems.AsReadOnly());

        async Task VerifyAccount()
        {
            var account = await rpc.Call("private/get_account_summary", new { currency = metadata.Currency, extended = true }, token);
            if (account.GetProperty("id").GetInt64() != binding.Plan.ExpectedAccountId ||
                account.GetProperty("type").GetString() != (binding.Plan.IsSubaccount ? "subaccount" : "main") ||
                account.GetProperty("currency").GetString() != metadata.Currency)
                throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.AccountMismatch);
        }

        async Task ReadSource(string name, bool historical, bool orders)
        {
            var open = name == "open";
            var method = open ? "private/get_open_orders_by_instrument" : orders
                ? "private/get_order_history_by_instrument" : "private/get_user_trades_by_instrument";
            var pageDigests = new HashSet<string>(StringComparer.Ordinal);
            long? nextSequence = null;
            long previousSequence = -1;
            var sourceIndex = sources.Count;
            sources.Add(new(name, 0, false));
            for (var page = 1; page <= query.MaximumPagesPerSource; page++)
            {
                var parameters = new Dictionary<string, object> { ["instrument_name"] = metadata.Name };
                if (!open)
                {
                    parameters["historical"] = historical; parameters["count"] = query.PageSize;
                    if (orders)
                    {
                        parameters["include_old"] = true; parameters["include_unfilled"] = true;
                        parameters["offset"] = (page - 1) * query.PageSize;
                    }
                    else
                    {
                        parameters["sorting"] = "asc";
                        parameters["end_timestamp"] = through.ToUnixTimeMilliseconds();
                        if (nextSequence.HasValue) parameters["start_seq"] = nextSequence.Value;
                        else parameters["start_timestamp"] = from.ToUnixTimeMilliseconds();
                    }
                }
                var result = await rpc.Call(method, parameters, token);
                var records = open || orders ? result : result.GetProperty("trades");
                if (records.ValueKind != JsonValueKind.Array) throw new FormatException();
                if (records.GetArrayLength() > (open ? query.MaximumFacts : query.PageSize))
                    throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.Capacity);
                sources[sourceIndex] = new(name, page, false);
                var received = DateTimeOffset.UtcNow;
                var pageKeys = new List<string>();
                foreach (var item in records.EnumerateArray())
                {
                    var execution = !open && !orders;
                    var facts = DeribitWebSocketMessageParser.ParsePrivateFacts(item, metadata, execution);
                    facts.Validate();
                    if (facts.VenueTimestampUtc > received || (open && facts.OrderState is not ("open" or "untriggered" or "triggered")))
                        throw new FormatException();
                    if (execution)
                    {
                        var seq = item.GetProperty("trade_seq").GetInt64();
                        if (seq < 0 || seq <= previousSequence || (nextSequence.HasValue && seq < nextSequence))
                            throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.Pagination);
                        previousSequence = seq;
                        if (facts.VenueTimestampUtc < from || facts.VenueTimestampUtc > through) throw new FormatException();
                    }
                    // Open orders can predate the outage. History is scanned to exhaustion and filtered locally.
                    if (!open && (facts.VenueTimestampUtc < from || facts.VenueTimestampUtc > through)) continue;
                    var envelope = new OmsPrivateEventEnvelope(query.Scope, facts,
                        new(Guid.NewGuid(), query.Epoch, received, OmsPrivateCaptureSource.RecoveryQuery, method));
                    envelope.Validate();
                    var canonical = OmsPrivateEventCanonical.Encode(envelope);
                    pageKeys.Add(canonical.EventId + ":" + Convert.ToHexString(canonical.Sha256));
                    var key = (canonical.Namespace, canonical.EventId);
                    if (distinct.TryGetValue(key, out var hash))
                    {
                        if (!hash.SequenceEqual(canonical.Sha256))
                            throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.FactCollision);
                        continue;
                    }
                    if (observations.Count >= query.MaximumFacts) throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.Capacity);
                    distinct.Add(key, canonical.Sha256); observations.Add(envelope);
                }
                bool more;
                if (open) more = false;
                else if (orders)
                {
                    more = records.GetArrayLength() == query.PageSize;
                    // Offset history is not snapshot-consistent. Detect repeated included pages,
                    // but never promote exhaustion to coherent window coverage.
                    if (pageKeys.Count > 0 && !pageDigests.Add(string.Join('|', pageKeys.Order(StringComparer.Ordinal))))
                        throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.Pagination);
                }
                else
                {
                    more = result.GetProperty("has_more").GetBoolean();
                    if (more) nextSequence = checked(previousSequence + 1);
                }
                if (!more) { sources[sourceIndex] = new(name, page, true); return; }
                if (records.GetArrayLength() == 0) throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.Pagination);
            }
            throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.Capacity);
        }
    }

    internal static bool ValidGrant(string scope, string session)
    {
        if (scope.Length > 2048) return false;
        var tokens = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Count(x => x == session) != 1 || tokens.Count(x => x == "expires:300") > 1 || tokens.Contains("connection")) return false;
        return DeribitPrivateReadOnlyScope.IsValidGrant(string.Join(' ', tokens.Where(x => x != "expires:300").Select(x => x == session ? "connection" : x)));
    }
}
