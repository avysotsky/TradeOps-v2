using System.Text.Json;
using TradeOps.Application.Oms;

namespace TradeOps.Connectors.Deribit;

/// <summary>Bounded read-only BTC/ETH inverse future or option observations. No persistence or recovery authority.</summary>
public sealed class DeribitAccountSnapshotReader(DeribitPrivateCaptureBinding binding, HttpClient http,
    TimeProvider? timeProvider = null) : IDeribitAccountSnapshotReader
{
    public async Task<DeribitAccountSnapshotResult> ReadAsync(DeribitAccountSnapshotRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = binding.Plan.Validate(binding.Options, binding.Scope);
        if (request.Scope != binding.Scope || request.Epoch == Guid.Empty || request.Currency is not ("BTC" or "ETH") ||
            request.Kind is not ("future" or "option") || request.MaximumPositions is < 1 or > 4096 ||
            request.MaximumObservationSpan < TimeSpan.FromSeconds(1) || request.MaximumObservationSpan > TimeSpan.FromSeconds(60) ||
            http.DefaultRequestHeaders.Authorization is not null || http.DefaultRequestHeaders.Contains("Cookie"))
            throw new ArgumentException("Invalid scoped account snapshot binding.");
        var manifest = binding.Plan.Instruments.Values.Where(x => x.Currency == request.Currency && x.Kind == request.Kind).ToArray();
        if (manifest.Length == 0 || manifest.Any(x => request.Kind == "future"
                ? x.AmountUnit != OmsPrivateAmountUnit.Usd || x.AmountCurrency != "USD"
                : x.AmountUnit != OmsPrivateAmountUnit.BaseCoin || x.AmountCurrency != request.Currency))
            throw new ArgumentException("Unsupported account snapshot instrument units.");
        var names = manifest.Select(x => x.Name).Order(StringComparer.Ordinal).ToArray();
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        var clock = timeProvider ?? TimeProvider.System;
        var started = clock.GetUtcNow(); var last = started;
        DeribitCurrencyBalance? balance = null;
        DeribitAccountObservationWindow? positionsWindow = null;
        var positions = new List<DeribitAccountPosition>(); var problems = new List<DeribitAccountSnapshotProblem>();
        var rpc = new DeribitRecoveryRpc(http, binding.Scope.Environment);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60)); var token = deadline.Token;
        try
        {
            var session = "session:account_snapshot_" + Guid.NewGuid().ToString("N");
            var auth = await rpc.Call("public/auth", new { grant_type = "client_credentials", client_id = binding.Options.ClientId,
                client_secret = binding.Options.ClientSecret,
                scope = session + " expires:300 trade:read account:read wallet:none block_trade:none block_rfq:none" }, token);
            var bearer = auth.GetProperty("access_token").GetString();
            if (!DeribitRecoveryQueries.ValidGrant(auth.GetProperty("scope").GetString()!, session) ||
                auth.GetProperty("expires_in").GetInt32() is < 120 or > 300 || string.IsNullOrWhiteSpace(bearer) ||
                bearer.Length > 8192 || bearer.Any(char.IsControl))
                throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.Authentication);
            rpc.Token = bearer;
            var before = Stamp();
            var account = await Summary(); var received = Stamp();
            balance = new(request.Currency, Number(account, "balance"), Number(account, "equity"),
                account.GetProperty("cross_collateral_enabled").GetBoolean(),
                account.GetProperty("portfolio_margining_enabled").GetBoolean(), new(before, received));
            before = Stamp();
            var rows = await rpc.Call("private/get_positions", new { currency = request.Currency, kind = request.Kind }, token);
            received = Stamp();
            if (rows.ValueKind != JsonValueKind.Array) throw new JsonException();
            if (rows.GetArrayLength() > request.MaximumPositions)
                throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.Capacity);
            positionsWindow = new(before, received);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows.EnumerateArray())
            {
                var name = row.GetProperty("instrument_name").GetString()!;
                if (!allowed.Contains(name)) { problems.Add(DeribitAccountSnapshotProblem.UnmappedInstrument); break; }
                if (!seen.Add(name) || row.GetProperty("kind").GetString() != request.Kind) throw new JsonException();
                if (row.GetProperty("user_id").GetInt64() != binding.Plan.ExpectedAccountId)
                    throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.AccountMismatch);
                var direction = row.GetProperty("direction").GetString()!;
                var size = Number(row, "size");
                decimal? baseSize = request.Kind == "future" ? Number(row, "size_currency") : null;
                if (!ValidDirection(direction, size) || baseSize.HasValue && !ValidDirection(direction, baseSize.Value))
                    throw new JsonException();
                positions.Add(new(name, request.Kind, direction, size, request.Kind == "future" ? "USD" : request.Currency,
                    baseSize, request.Currency));
            }
            // Bracket all reads with native account verification. Equality of these identities is not snapshot coherence.
            await Summary(); Stamp();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { problems.Add(DeribitAccountSnapshotProblem.Deadline); }
        catch (DeribitRecoveryQueryException e)
        {
            problems.Add(e.Problem switch {
                DeribitRecoveryQueryProblem.Authentication => DeribitAccountSnapshotProblem.Authentication,
                DeribitRecoveryQueryProblem.AccountMismatch => DeribitAccountSnapshotProblem.AccountMismatch,
                DeribitRecoveryQueryProblem.Capacity => DeribitAccountSnapshotProblem.Capacity,
                DeribitRecoveryQueryProblem.Transport => DeribitAccountSnapshotProblem.Transport,
                _ => DeribitAccountSnapshotProblem.Protocol });
        }
        catch (Exception e) when (e is HttpRequestException or IOException) { problems.Add(DeribitAccountSnapshotProblem.Transport); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException)
        { problems.Add(DeribitAccountSnapshotProblem.Protocol); }
        finally { rpc.Token = null; }
        var completed = clock.GetUtcNow();
        if (completed < last || completed - started > request.MaximumObservationSpan)
            problems.Add(DeribitAccountSnapshotProblem.StaleObservation);
        if (problems.Count != 0) { balance = null; positionsWindow = null; positions.Clear(); }
        return new(request.Scope, request.Epoch, request.Currency, request.Kind, started, completed, request.MaximumObservationSpan,
            Array.AsReadOnly(names), balance, positionsWindow, positions.AsReadOnly(), problems.Distinct().ToArray());

        DateTimeOffset Stamp()
        {
            var now = clock.GetUtcNow();
            if (now < last || now - started > request.MaximumObservationSpan)
                problems.Add(DeribitAccountSnapshotProblem.StaleObservation);
            last = now; return now;
        }
        async Task<JsonElement> Summary()
        {
            var result = await rpc.Call("private/get_account_summary", new { currency = request.Currency, extended = true }, token);
            if (result.GetProperty("id").GetInt64() != binding.Plan.ExpectedAccountId ||
                result.GetProperty("type").GetString() != (binding.Plan.IsSubaccount ? "subaccount" : "main") ||
                result.GetProperty("currency").GetString() != request.Currency)
                throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.AccountMismatch);
            return result;
        }
    }

    private static decimal Number(JsonElement row, string name) => row.GetProperty(name).GetDecimal();
    private static bool ValidDirection(string direction, decimal size) => direction switch {
        "buy" => size > 0, "sell" => size < 0, "zero" => size == 0, _ => false };
}
