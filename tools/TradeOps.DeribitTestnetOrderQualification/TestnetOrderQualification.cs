using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.QualificationSupport;
using CaptureRole = TradeOps.DeribitLiveInboxQualification.LiveInboxQualification.CaptureRole;

namespace TradeOps.DeribitTestnetOrderQualification;

// Not a record: generated ToString must never print keys.
internal sealed class TestnetOrderInput
{
    public string Purpose { get; set; } = "";
    public string Environment { get; set; } = "";
    public bool ApprovedOneOrderAndCancellation { get; set; }
    public bool DedicatedTestAccount { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public long ExpectedAccountId { get; set; }
    public string AccountType { get; set; } = "";
    internal void Validate()
    {
        if (Purpose != "TESTNET_ONE_POSTONLY_ORDER_CANCEL" || Environment != "Testnet" ||
            !ApprovedOneOrderAndCancellation || !DedicatedTestAccount || ExpectedAccountId < 0 ||
            AccountType is not ("" or "main" or "subaccount") || string.IsNullOrWhiteSpace(ClientId) ||
            string.IsNullOrWhiteSpace(ClientSecret) || ClientId.Length > 512 || ClientSecret.Length > 4096 ||
            ClientId.Any(char.IsControl) || ClientSecret.Any(char.IsControl))
            throw new InvalidOperationException("Explicit dedicated testnet qualification required.");
    }
}

internal sealed record TestnetOrderResult(string Status, bool? DatabaseUsed, int PlacementAttempts,
    int CancellationAttempts, bool ExchangeOrderResolved, bool OpenEventCommitted, bool CancelEventCommitted,
    bool UnexpectedFill, long? IncidentRows, bool TradingReady = false, string Environment = "Testnet");

internal static class TestnetOrderQualification
{
    internal const string Instrument = "BTC-PERPETUAL";
    internal const decimal MaximumNativeUsd = 10m;
    internal static async Task<TestnetOrderResult> RunAsync(TestnetOrderInput input, HttpClient http,
        string attemptDirectory, CancellationToken ct,
        Func<DeribitOptions, DeribitPrivateWebSocketStream>? streamFactory = null)
    {
        input.Validate(); // Before filesystem, DB or network access.
        Directory.CreateDirectory(attemptDirectory);
        // One running acceptance process in this local directory. No claim of production sender fencing.
        using var exclusive = new FileStream(Path.Combine(attemptDirectory, "run.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        // Never start a second order while the previous attempt has no proved exchange resolution.
        var markerPath = Path.Combine(attemptDirectory, "attempt.json");
        if (File.Exists(markerPath)) throw new InvalidOperationException("Previous testnet attempt needs reconciliation.");
        var rpc = new TestnetRpc(http);
        var auth = await rpc.Call("public/auth", new { grant_type = "client_credentials", client_id = input.ClientId,
            client_secret = input.ClientSecret,
            scope = "connection trade:read_write account:read wallet:none block_trade:none block_rfq:none" }, ct);
        ValidateGrant(auth.GetProperty("scope").GetString()!);
        rpc.Token = auth.GetProperty("access_token").GetString() ?? throw new InvalidOperationException();
        var identity = await rpc.Call("private/get_account_summary", new { currency = "BTC", extended = true }, ct);
        var nativeId = identity.GetProperty("id").GetInt64();
        var accountType = identity.GetProperty("type").GetString();
        if (nativeId <= 0 || accountType is not ("main" or "subaccount") ||
            input.ExpectedAccountId != 0 && nativeId != input.ExpectedAccountId ||
            input.AccountType.Length != 0 && accountType != input.AccountType)
            throw new InvalidOperationException("Testnet account pin failed.");
        var orders = await rpc.Call("private/get_open_orders_by_currency", new { currency = "BTC" }, ct);
        var positions = await rpc.Call("private/get_positions", new { currency = "BTC" }, ct);
        if (orders.GetArrayLength() != 0 || positions.EnumerateArray().Any(x => x.GetProperty("size").GetDecimal() != 0))
            throw new InvalidOperationException("An idle dedicated BTC test account is required.");
        var instrument = await rpc.Call("public/get_instrument", new { instrument_name = Instrument }, ct);
        var book = await rpc.Call("public/get_order_book", new { instrument_name = Instrument, depth = 1 }, ct);
        var terms = ValidateRisk(instrument, book, DateTimeOffset.UtcNow);
        var native = "observed-native:" + nativeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var scope = new AccountKey(new("local-testnet-qualification"), new("DERIBIT"), OmsEnvironment.Testnet,
            new(native), accountType == "subaccount" ? new TradingSubaccountId(native) : null);
        await using var database = await OmsPostgresTestDatabase.CreateAsync();
        await using var role = await CaptureRole.CreateAsync(database, ct);
        await using var owner = await OmsPostgresPrivateCaptureOwner.AcquireAsync(role.Source, scope, ct);
        await using var coordinator = new OmsPrivateCaptureCoordinator(owner);
        var journal = new OmsPostgresJournal(database.Source);
        var plan = new DeribitPrivateCapturePlan(nativeId, accountType == "subaccount",
            new[] { new DeribitPrivateInstrument(Instrument, "future", "BTC", OmsPrivateAmountUnit.Usd, "USD") });
        var settings = new DeribitOptions { WebSocketUrl = "wss://test.deribit.com/ws/api/v2",
            ClientId = input.ClientId, ClientSecret = input.ClientSecret };
        var stream = streamFactory?.Invoke(settings) ?? new(settings, NullLogger<DeribitPrivateWebSocketStream>.Instance);
        var diagnostics = new DeribitPrivateCaptureDiagnostics();
        var session = await coordinator.SwitchAsync(scope, ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var run = stream.RunCaptureAsync(coordinator, session, plan, stop.Token, diagnostics);
        var label = "to-b3-" + Guid.NewGuid().ToString("N");
        var local = new OmsLocalOrderId(Guid.NewGuid());
        var intent = new OrderIntentId(Guid.NewGuid());
        var placements = 0; var cancellations = 0; var resolved = false; var accepted = false;
        string? exchangeOrderId = null;
        var openCommitted = false; var cancelCommitted = false; var unexpectedFill = false;
        var successfulShutdown = false;
        try
        {
            await WaitAsync(() => Task.FromResult(diagnostics.Snapshot.SubscriptionsConfirmed == 1), run,
                diagnostics, TimeSpan.FromSeconds(30), ct);
            instrument = await rpc.Call("public/get_instrument", new { instrument_name = Instrument }, ct);
            book = await rpc.Call("public/get_order_book", new { instrument_name = Instrument, depth = 1 }, ct);
            terms = ValidateRisk(instrument, book, DateTimeOffset.UtcNow);
            // A non-secret recovery marker is flushed BEFORE dispatch. Never delete it after uncertainty.
            using (var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                JsonSerializer.Serialize(marker, new { environment = "Testnet", instrument = Instrument,
                    label, state = "MAY_HAVE_BEEN_SENT", createdAtUtc = DateTimeOffset.UtcNow });
                marker.Flush(true);
            }
            var command = Request(OmsJournalCommandKind.Place, 0);
            await Start(command, ct);
            placements = 1;
            try
            {
                // Exactly one send. No retries after timeout, cancellation or an ambiguous response.
                var reply = await rpc.Call("private/buy", new { instrument_name = Instrument,
                    amount = terms.Amount, type = "limit", price = terms.Price, time_in_force = "good_til_cancelled",
                    post_only = true, reject_post_only = true, label }, ct);
                var order = reply.GetProperty("order");
                VerifyOrder(order, label, terms.Amount);
                exchangeOrderId = order.GetProperty("order_id").GetString();
                accepted = order.GetProperty("order_state").GetString() == "open";
                unexpectedFill |= order.GetProperty("filled_amount").GetDecimal() != 0;
                await journal.RecordOutcomeAsync(scope, command.CommandId, OmsJournalDispatchOutcome.Acknowledged, ct);
            }
            catch
            {
                using var save = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await journal.RecordOutcomeAsync(scope, command.CommandId, OmsJournalDispatchOutcome.Ambiguous, save.Token);
                throw;
            }
            await WaitAsync(async () => await CountState("open", ct) > 0, run, diagnostics, TimeSpan.FromSeconds(10), ct);
            openCommitted = true;
        }
        catch { /* Never render a private RPC/SQL payload. Cleanup still runs with an independent deadline. */ }
        finally
        {
            if (placements != 0)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    // Cancellation targets this run's unique label in BTC, including a lost placement ACK.
                    var cancel = Request(OmsJournalCommandKind.Cancel, 1);
                    await Start(cancel, cleanup.Token);
                    cancellations = 1;
                    try
                    {
                        var reply = await rpc.Call("private/cancel_by_label", new { currency = "BTC", label }, cleanup.Token);
                        if (reply.GetInt32() is < 0 or > 1) throw new InvalidOperationException();
                        await journal.RecordOutcomeAsync(scope, cancel.CommandId, OmsJournalDispatchOutcome.Acknowledged, cleanup.Token);
                    }
                    catch
                    {
                        await journal.RecordOutcomeAsync(scope, cancel.CommandId, OmsJournalDispatchOutcome.Ambiguous, cleanup.Token);
                        // A lost cancel ACK is resolved only by querying explicit order state below.
                    }
                    var states = await rpc.Call("private/get_order_state_by_label", new { currency = "BTC", label }, cleanup.Token);
                    if (states.GetArrayLength() != 1) throw new InvalidOperationException();
                    var state = states[0]; VerifyOrder(state, label, terms.Amount);
                    if (exchangeOrderId is not null && exchangeOrderId != state.GetProperty("order_id").GetString())
                        throw new InvalidOperationException("Resolved order identity differs.");
                    exchangeOrderId ??= state.GetProperty("order_id").GetString();
                    unexpectedFill |= state.GetProperty("filled_amount").GetDecimal() != 0;
                    resolved = state.GetProperty("order_state").GetString() is "cancelled" or "filled" or "rejected";
                    if (state.GetProperty("order_state").GetString() == "cancelled" && !unexpectedFill)
                    {
                        await WaitAsync(async () => await CountState("cancelled", cleanup.Token) > 0, run,
                            diagnostics, TimeSpan.FromSeconds(10), cleanup.Token);
                        cancelCommitted = true;
                    }
                }
                catch { /* Marker remains if the exchange state is unresolved. */ }
            }
            stop.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(20)); successfulShutdown = true; } catch { }
        }
        using var audit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var auditConnection = await role.Source.OpenConnectionAsync(audit.Token);
        await using var incidentsQuery = new NpgsqlCommand("SELECT count(*) FROM oms_integrity_incidents", auditConnection);
        var incidents = (long)(await incidentsQuery.ExecuteScalarAsync(audit.Token))!;
        await using var fillsQuery = new NpgsqlCommand("""
SELECT EXISTS(SELECT 1 FROM oms_inbox WHERE schema_version=2 AND
 event_json->'facts'->>'clientLabel'=@label AND
 (event_kind='Execution' OR (event_json->'facts'->>'filledNativeAmount')::numeric>0))
""", auditConnection);
        fillsQuery.Parameters.AddWithValue("label", label);
        unexpectedFill |= (bool)(await fillsQuery.ExecuteScalarAsync(audit.Token))!;
        var d = diagnostics.Snapshot;
        var pass = accepted && resolved && openCommitted && cancelCommitted && !unexpectedFill &&
            successfulShutdown && incidents == 0 && d.Epochs == 1 && d.Problems == 0 &&
            d.Authenticated == 1 && d.AccountsVerified == 1 && d.SubscriptionsConfirmed == 1;
        // Keep the attempt marker even after resolution; launcher archives it before an explicitly new run.
        if (resolved) File.WriteAllText(markerPath, JsonSerializer.Serialize(new { environment = "Testnet",
            instrument = Instrument, label, state = unexpectedFill ? "RESOLVED_WITH_FILL" : "RESOLVED", pass }));
        return new(pass ? "TESTNET_ORDER_CAPTURE_PASS" : resolved ? "TESTNET_ORDER_CAPTURE_FAILED" :
            "TESTNET_ORDER_UNRESOLVED", true, placements, cancellations, resolved, openCommitted,
            cancelCommitted, unexpectedFill, incidents);

        OmsJournalCommandRequest Request(OmsJournalCommandKind kind, long version) => new(scope, local, intent,
            new(Guid.NewGuid()), new(version), new(kind, Instrument, OrderSide.Buy, OrderType.Limit,
                terms.Amount, OmsQuantityUnit.QuoteAsset, terms.Price, null, "good_til_cancelled", false, true,
                "testnet-one-order-acceptance", "owner-approved-testnet", "bounded-testnet-usd10:" + label,
                DateTimeOffset.UtcNow, TargetClientOrderId: label));
        async Task Start(OmsJournalCommandRequest request, CancellationToken token)
        {
            if (await journal.PersistAsync(request, cancellationToken: token) != OmsJournalWriteResult.Created ||
                await journal.MarkDispatchStartedAsync(scope, request.CommandId, cancellationToken: token) != OmsJournalAttemptResult.Started)
                throw new InvalidOperationException("Durable dispatch boundary refused.");
        }
        async Task<long> CountState(string state, CancellationToken token)
        {
            await using var db = await role.Source.OpenConnectionAsync(token);
            await using var query = new NpgsqlCommand("""
SELECT count(*) FROM oms_inbox WHERE schema_version=2 AND environment=3
 AND event_json->'facts'->>'clientLabel'=@label
 AND event_json->'facts'->>'nativeInstrument'='BTC-PERPETUAL'
 AND event_json->'facts'->>'orderState'=@state
 AND (@orderId='' OR event_json->'facts'->>'exchangeOrderId'=@orderId)
 AND (event_json->'facts'->>'filledNativeAmount')::numeric=0
""", db);
            query.Parameters.AddWithValue("label", label); query.Parameters.AddWithValue("state", state);
            query.Parameters.AddWithValue("orderId", exchangeOrderId ?? "");
            return (long)(await query.ExecuteScalarAsync(token))!;
        }
    }

    internal static (decimal Amount, decimal Price) ValidateRisk(JsonElement metadata, JsonElement book, DateTimeOffset now)
    {
        if (metadata.GetProperty("instrument_name").GetString() != Instrument ||
            metadata.GetProperty("kind").GetString() != "future" || metadata.GetProperty("instrument_type").GetString() != "reversed" ||
            metadata.GetProperty("base_currency").GetString() != "BTC" || metadata.GetProperty("quote_currency").GetString() != "USD" ||
            metadata.GetProperty("settlement_currency").GetString() != "BTC" || !metadata.GetProperty("is_active").GetBoolean() ||
            book.GetProperty("instrument_name").GetString() != Instrument || book.GetProperty("state").GetString() != "open")
            throw new InvalidOperationException("Unsupported test instrument.");
        if (metadata.TryGetProperty("tick_size_steps", out var steps) && steps.GetArrayLength() != 0)
            throw new InvalidOperationException("Stepped tick sizes require separate qualification.");
        var amount = metadata.GetProperty("min_trade_amount").GetDecimal();
        var tick = metadata.GetProperty("tick_size").GetDecimal();
        var bid = book.GetProperty("best_bid_price").GetDecimal();
        var ask = book.GetProperty("best_ask_price").GetDecimal();
        var minimum = book.GetProperty("min_price").GetDecimal();
        var maximum = book.GetProperty("max_price").GetDecimal();
        var age = now - DateTimeOffset.FromUnixTimeMilliseconds(book.GetProperty("timestamp").GetInt64());
        if (amount <= 0 || amount > MaximumNativeUsd || tick <= 0 || bid <= 0 || ask <= bid ||
            minimum <= 0 || maximum < ask || age < TimeSpan.FromSeconds(-2) || age > TimeSpan.FromSeconds(5))
            throw new InvalidOperationException("Bounded testnet risk check failed.");
        var price = decimal.Floor(bid * 0.995m / tick) * tick;
        if (price < minimum || price >= bid || price >= ask || price > maximum)
            throw new InvalidOperationException("No qualified passive test price.");
        return (amount, price);
    }
    internal static void ValidateGrant(string scope)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in scope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = item.Split(':')[0];
            if (!seen.Add(key) || (item is not ("connection" or "trade:read_write" or "account:read" or
                "wallet:none" or "block_trade:none" or "block_rfq:none" or "mainaccount" or "subaccount") &&
                !(item.StartsWith("name:", StringComparison.Ordinal) && item.Length is > 5 and <= 160 && !item.Any(char.IsControl))))
                throw new InvalidOperationException("Unexpected testnet grant.");
        }
        if (seen.Contains("mainaccount") && seen.Contains("subaccount") ||
            !scope.Split(' ').Contains("connection") || !scope.Split(' ').Contains("trade:read_write") || !scope.Split(' ').Contains("account:read"))
            throw new InvalidOperationException("Required testnet grant missing.");
    }
    private static void VerifyOrder(JsonElement order, string label, decimal amount)
    {
        if (order.GetProperty("label").GetString() != label || order.GetProperty("instrument_name").GetString() != Instrument ||
            order.GetProperty("direction").GetString() != "buy" || order.GetProperty("amount").GetDecimal() != amount ||
            string.IsNullOrWhiteSpace(order.GetProperty("order_id").GetString())) throw new InvalidOperationException("Order pin failed.");
    }
    private static async Task WaitAsync(Func<Task<bool>> ready, Task stream, DeribitPrivateCaptureDiagnostics diagnostics,
        TimeSpan timeout, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (!await ready())
        {
            if (stream.IsCompleted || diagnostics.Snapshot.Problems != 0 || clock.Elapsed >= timeout)
                throw new InvalidOperationException("Testnet capture milestone missing.");
            await Task.Delay(25, ct);
        }
    }
}
