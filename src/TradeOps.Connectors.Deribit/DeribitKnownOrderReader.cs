using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Connectors.Deribit;

/// <summary>Addressed current-state reads only. Missing old orders never manufacture terminal states.</summary>
public sealed class DeribitKnownOrderReader(DeribitPrivateCaptureBinding binding, HttpClient http) : IDeribitKnownOrderReader
{
    public async Task<DeribitKnownOrderReadResult> ReadAsync(DeribitKnownOrderReadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = binding.Plan.Validate(binding.Options, binding.Scope);
        // Snapshot the caller's bounded collection before any asynchronous operation.
        if (request.Orders is null || request.Orders.Count is < 1 or > 32 || request.Scope != binding.Scope ||
            request.Epoch == Guid.Empty || request.SnapshotId == Guid.Empty || http.DefaultRequestHeaders.Authorization is not null ||
            http.DefaultRequestHeaders.Contains("Cookie")) throw new ArgumentException("Invalid known-order binding.");
        var orders = request.Orders.ToArray();
        foreach (var seed in orders)
        {
            seed.Validate();
            if (seed.Kind != OmsJournalEventKind.Order || !binding.Plan.Instruments.TryGetValue(seed.NativeInstrument, out var metadata) ||
                seed.Currency != metadata.Currency || seed.AmountUnit != metadata.AmountUnit || seed.AmountCurrency != metadata.AmountCurrency ||
                seed.VenueTimestampUtc > DateTimeOffset.UtcNow) throw new ArgumentException("Invalid retained order reference.");
        }
        if (orders.Select(x => (x.Currency, x.ExchangeOrderId)).Distinct().Count() != orders.Length)
            throw new ArgumentException("Duplicate known-order reference.");
        var started = DateTimeOffset.UtcNow;
        var lookups = new List<DeribitKnownOrderLookup>(); var observations = new List<OmsPrivateEventEnvelope>();
        var problems = new List<DeribitKnownOrderReadProblem>(); int? httpStatus = null;
        var rpc = new DeribitRecoveryRpc(http, binding.Scope.Environment);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        try
        {
            var session = "session:known_orders_" + Guid.NewGuid().ToString("N");
            var auth = await rpc.Call("public/auth", new { grant_type = "client_credentials", client_id = binding.Options.ClientId,
                client_secret = binding.Options.ClientSecret,
                scope = session + " expires:300 trade:read account:read wallet:none block_trade:none block_rfq:none" }, token);
            var bearer = auth.GetProperty("access_token").GetString();
            if (!DeribitRecoveryQueries.ValidGrant(auth.GetProperty("scope").GetString()!, session) ||
                auth.GetProperty("expires_in").GetInt32() is < 120 or > 300 || string.IsNullOrWhiteSpace(bearer) ||
                bearer.Length > 8192 || bearer.Any(char.IsControl))
                throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.Authentication);
            rpc.Token = bearer;
            await Verify();
            for (var i = 0; i < orders.Length; i++)
            {
                var seed = orders[i]; JsonElement item;
                try { item = await rpc.Call("private/get_order_state", new { order_id = seed.ExchangeOrderId }, token); }
                catch (DeribitRecoveryQueryException e) when (e.VenueCode is 10004 or 10033 or -32601)
                {
                    lookups.Add(new(i, e.VenueCode == 10004 ? DeribitKnownOrderLookupOutcome.Missing : DeribitKnownOrderLookupOutcome.Unsupported, e.VenueCode));
                    continue; // Explicit missing/unsupported. No historical-flag fallback or retry.
                }
                var facts = DeribitWebSocketMessageParser.ParsePrivateFacts(item, binding.Plan.Instruments[seed.NativeInstrument], false);
                facts.Validate();
                var received = DateTimeOffset.UtcNow;
                if (facts.ExchangeOrderId != seed.ExchangeOrderId || facts.Side != seed.Side || facts.NativeAmount != seed.NativeAmount ||
                    facts.VenueTimestampUtc < seed.VenueTimestampUtc || facts.VenueTimestampUtc > received ||
                    facts.FilledNativeAmount is null || facts.FilledNativeAmount < seed.FilledNativeAmount ||
                    (Terminal(seed.OrderState) && !Terminal(facts.OrderState)) ||
                    (item.TryGetProperty("quote_id", out var quote) && quote.ValueKind != JsonValueKind.Null))
                    throw new InvalidDataException();
                var envelope = new OmsPrivateEventEnvelope(request.Scope, facts,
                    new(Guid.NewGuid(), request.Epoch, received, OmsPrivateCaptureSource.RecoveryQuery, "private/get_order_state"));
                envelope.Validate(); observations.Add(envelope);
                lookups.Add(new(i, DeribitKnownOrderLookupOutcome.Found));
            }
            await Verify();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { problems.Add(DeribitKnownOrderReadProblem.Deadline); }
        catch (InvalidDataException) { problems.Add(DeribitKnownOrderReadProblem.EvidenceMismatch); }
        catch (DeribitRecoveryQueryException e)
        {
            problems.Add(e.Problem switch {
                DeribitRecoveryQueryProblem.Authentication => DeribitKnownOrderReadProblem.Authentication,
                DeribitRecoveryQueryProblem.AccountMismatch => DeribitKnownOrderReadProblem.AccountMismatch,
                DeribitRecoveryQueryProblem.Capacity => DeribitKnownOrderReadProblem.Capacity,
                DeribitRecoveryQueryProblem.Transport => DeribitKnownOrderReadProblem.Transport,
                _ => DeribitKnownOrderReadProblem.Protocol });
            httpStatus = e.HttpStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or IOException) { problems.Add(DeribitKnownOrderReadProblem.Transport); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or FormatException or OverflowException)
        { problems.Add(DeribitKnownOrderReadProblem.Protocol); }
        finally { rpc.Token = null; }
        if (problems.Count != 0 || lookups.Any(x => x.Outcome != DeribitKnownOrderLookupOutcome.Found)) observations.Clear();
        return new(request.Scope, request.Epoch, request.SnapshotId, started, DateTimeOffset.UtcNow, orders.Length,
            lookups.AsReadOnly(), observations.AsReadOnly(), problems.AsReadOnly(), httpStatus);

        async Task Verify()
        {
            var currency = orders[0].Currency;
            var account = await rpc.Call("private/get_account_summary", new { currency, extended = true }, token);
            if (account.GetProperty("id").GetInt64() != binding.Plan.ExpectedAccountId ||
                account.GetProperty("type").GetString() != (binding.Plan.IsSubaccount ? "subaccount" : "main") ||
                account.GetProperty("currency").GetString() != currency)
                throw new DeribitRecoveryQueryException(DeribitRecoveryQueryProblem.AccountMismatch);
        }
    }

    private static bool Terminal(string? state) => state is "cancelled" or "filled" or "rejected";
}
