using System.Text.Json;
using TradeOps.Application.Oms;

namespace TradeOps.Connectors.Deribit;

/// <summary>Discovers current account-wide activity, including unknown instruments and unsupported products.
/// No manifest filter, quantity conversion, synthetic zero holdings, history coverage or readiness authority.</summary>
public sealed class DeribitAccountInventoryReader(DeribitPrivateCaptureBinding binding, HttpClient http,
    TimeProvider? timeProvider = null) : IDeribitAccountInventoryReader
{
    public async Task<DeribitAccountInventoryResult> ReadAsync(DeribitAccountInventoryRequest q, CancellationToken ct = default)
    {
        _ = binding.Plan.Validate(binding.Options, binding.Scope);
        if (q.Scope != binding.Scope || q.Epoch == Guid.Empty || q.MaximumObservationSpan < TimeSpan.FromSeconds(1) ||
            q.MaximumObservationSpan > TimeSpan.FromSeconds(60) || q.MaximumPositions is < 1 or > 4096 ||
            q.MaximumOpenOrders is < 1 or > 4096 || q.MaximumCurrencies is < 1 or > 32 ||
            http.DefaultRequestHeaders.Authorization is not null || http.DefaultRequestHeaders.Contains("Cookie"))
            throw new ArgumentException("Invalid account inventory request/binding.");
        var clock = timeProvider ?? TimeProvider.System; var started = clock.GetUtcNow(); var last = started;
        var balances = new List<DeribitCurrencyBalance>(); var positions = new List<DeribitNativePositionReference>();
        var orders = new List<DeribitNativeOpenOrderReference>(); var problems = new List<DeribitAccountInventoryProblem>();
        var rpc = new DeribitRecoveryRpc(http, q.Scope.Environment);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(q.MaximumObservationSpan);
        var token = deadline.Token;
        try
        {
            var session = "session:account_inventory_" + Guid.NewGuid().ToString("N");
            var auth = await rpc.Call("public/auth", new { grant_type = "client_credentials", client_id = binding.Options.ClientId,
                client_secret = binding.Options.ClientSecret,
                scope = session + " expires:300 trade:read account:read wallet:none block_trade:none block_rfq:none" }, token);
            var bearer = auth.GetProperty("access_token").GetString();
            if (!DeribitRecoveryQueries.ValidGrant(auth.GetProperty("scope").GetString()!, session) ||
                auth.GetProperty("expires_in").GetInt32() is < 120 or > 300 || string.IsNullOrWhiteSpace(bearer) ||
                bearer.Length > 8192 || bearer.Any(char.IsControl)) throw new Refused(DeribitAccountInventoryProblem.AccountMismatch);
            rpc.Token = bearer;
            await Summary();
            var rawPositions = await rpc.Call("private/get_positions", new { currency = "any" }, token); Stamp();
            Array(rawPositions, q.MaximumPositions); var seenPositions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rawPositions.EnumerateArray())
            {
                if (row.GetProperty("user_id").GetInt64() != binding.Plan.ExpectedAccountId) throw new Refused(DeribitAccountInventoryProblem.AccountMismatch);
                var symbol = Text(row, "instrument_name", 160); var kind = Text(row, "kind", 32);
                var size = row.GetProperty("size").GetDecimal(); var direction = Text(row, "direction", 16);
                if (!seenPositions.Add(symbol) || (direction switch { "buy" => size <= 0, "sell" => size >= 0, "zero" => size != 0, _ => true }))
                    throw new Refused(DeribitAccountInventoryProblem.InvalidObservation);
                decimal? baseSize = row.TryGetProperty("size_currency", out var nativeBase) && nativeBase.ValueKind != JsonValueKind.Null ? nativeBase.GetDecimal() : null;
                positions.Add(new(symbol, kind, direction, size, baseSize));
            }
            // No kind/type/currency filter: trigger, linked and other product families must remain visible.
            var rawOrders = await rpc.Call("private/get_open_orders", new { }, token); Stamp();
            Array(rawOrders, q.MaximumOpenOrders); var seenOrders = new HashSet<(string, string)>();
            foreach (var row in rawOrders.EnumerateArray())
            {
                var symbol = Text(row, "instrument_name", 160); var id = Text(row, "order_id", 160);
                var state = Text(row, "order_state", 32); var type = Text(row, "order_type", 32);
                if (state is not ("open" or "triggered" or "untriggered") || !seenOrders.Add((symbol, id)))
                    throw new Refused(DeribitAccountInventoryProblem.InvalidObservation);
                orders.Add(new(symbol, id, state, type, DeribitNativeOrderFingerprint.Create(row)));
            }
            await Summary(); // Refresh actual balance observations and recheck the native identity.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Refused error) { problems.Add(error.Problem); }
        catch (DeribitRecoveryQueryException error) { problems.Add(error.Problem == DeribitRecoveryQueryProblem.Capacity
            ? DeribitAccountInventoryProblem.Capacity : DeribitAccountInventoryProblem.ReadFailed); }
        catch (Exception error) when (error is OperationCanceledException or JsonException or HttpRequestException or IOException or
            InvalidOperationException or KeyNotFoundException or ArgumentException or FormatException or OverflowException)
        { problems.Add(DeribitAccountInventoryProblem.ReadFailed); }
        finally { rpc.Token = null; }
        var completed = clock.GetUtcNow();
        if (completed < last || completed-started > q.MaximumObservationSpan) problems.Add(DeribitAccountInventoryProblem.StaleObservation);
        if (problems.Count != 0) { balances.Clear(); positions.Clear(); orders.Clear(); }
        return new(q.Scope, q.Epoch, started, completed, q.MaximumObservationSpan,
            balances.AsReadOnly(), positions.AsReadOnly(), orders.AsReadOnly(), problems.AsReadOnly());

        async Task Summary()
        {
            var before = Stamp(); var summary = await rpc.Call("private/get_account_summaries", new { extended = true }, token); var received = Stamp();
            if (summary.GetProperty("id").GetInt64() != binding.Plan.ExpectedAccountId ||
                summary.GetProperty("type").GetString() != (binding.Plan.IsSubaccount ? "subaccount" : "main"))
                throw new Refused(DeribitAccountInventoryProblem.AccountMismatch);
            var rows = summary.GetProperty("summaries"); Array(rows, q.MaximumCurrencies);
            if (rows.GetArrayLength() == 0) throw new Refused(DeribitAccountInventoryProblem.InvalidObservation);
            var seen = new HashSet<string>(StringComparer.Ordinal); var fresh = new List<DeribitCurrencyBalance>();
            foreach (var row in rows.EnumerateArray())
            {
                var currency = Text(row, "currency", 32);
                if (currency.Any(c => !(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) || !seen.Add(currency))
                    throw new Refused(DeribitAccountInventoryProblem.InvalidObservation);
                fresh.Add(new(currency, row.GetProperty("balance").GetDecimal(), row.GetProperty("equity").GetDecimal(),
                    row.GetProperty("cross_collateral_enabled").GetBoolean(), row.GetProperty("portfolio_margining_enabled").GetBoolean(), new(before, received)));
            }
            if (balances.Count != 0 && !balances.Select(x => x.Currency).Order(StringComparer.Ordinal)
                .SequenceEqual(fresh.Select(x => x.Currency).Order(StringComparer.Ordinal)))
                throw new Refused(DeribitAccountInventoryProblem.InvalidObservation);
            balances.Clear(); balances.AddRange(fresh);
        }
        DateTimeOffset Stamp()
        {
            var at = clock.GetUtcNow();
            if (at < last || at-started > q.MaximumObservationSpan) throw new Refused(DeribitAccountInventoryProblem.StaleObservation);
            last = at; return at;
        }
    }
    private static void Array(JsonElement rows, int maximum)
    {
        if (rows.ValueKind != JsonValueKind.Array) throw new FormatException();
        if (rows.GetArrayLength() > maximum) throw new Refused(DeribitAccountInventoryProblem.Capacity);
    }
    private static string Text(JsonElement row, string name, int maximum)
    {
        var text = row.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text != text.Trim() || text.Any(char.IsControl)) throw new FormatException();
        return text;
    }
    private sealed class Refused(DeribitAccountInventoryProblem problem) : Exception
    { internal DeribitAccountInventoryProblem Problem { get; } = problem; }
}
