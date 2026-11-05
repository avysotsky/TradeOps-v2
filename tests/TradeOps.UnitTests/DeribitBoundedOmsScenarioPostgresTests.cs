using System.Net;
using System.Text;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.DeribitMainnetEthQualification;
using TradeOps.Infrastructure.Persistence.Oms;
using OmsPostgresTestDatabase = TradeOps.QualificationSupport.OmsPostgresTestDatabase;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;

public sealed class DeribitBoundedOmsScenarioPostgresTests
{
    [Fact]
    public async Task QuoteExpiringDuringClockCheckCannotReachPhysicalPlacement()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        await using var capture = await TradeOps.DeribitLiveInboxQualification.LiveInboxQualification.CaptureRole.CreateAsync(db, CancellationToken.None);
        var socket = new Socket(); var venue = new Venue(socket, "quote-expired"); using var http = new HttpClient(venue);
        var directory = Path.Combine(Path.GetTempPath(), "tradeops-quote-expiry-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await BoundedOmsScenario.RunAsync(Input(), http, directory, timeout.Token,
                _ => Task.FromResult(new BoundedOmsStore(db.Source, capture.Source, db.Source, false, new Noop())), options => Stream(() => socket, options));
            Assert.Equal("OMS_SCENARIO_REFUSED", result.Status); Assert.Equal(0, venue.Placements); Assert.Equal(0, venue.Cancellations);
            Assert.Equal(1, result.StagePlacementsUsed); Assert.False(result.OwnOrderMayRemain);
        }
        finally { File.Delete(Path.Combine(directory, "owner-run.lock")); Directory.Delete(directory); }
    }

    [Fact]
    public async Task ThreeCompletedRunsUsePersistentBudgetAndFourthCannotSend()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        await using var capture = await TradeOps.DeribitLiveInboxQualification.LiveInboxQualification.CaptureRole.CreateAsync(db, CancellationToken.None);
        var history = new Dictionary<string, JsonElement>();
        for (var i = 1; i <= 4; i++)
        {
            var socket = new Socket(); var venue = new Venue(socket, "normal", history); using var http = new HttpClient(venue);
            var directory = Path.Combine(Path.GetTempPath(), "tradeops-budget-run-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var result = await BoundedOmsScenario.RunAsync(Input(), http, directory, timeout.Token,
                    _ => Task.FromResult(new BoundedOmsStore(db.Source, capture.Source, db.Source, false, new Noop())), options => Stream(() => socket, options));
                Assert.Equal(i <= 3 ? "OMS_SCENARIO_PASS" : "OMS_SCENARIO_REFUSED", result.Status);
                Assert.Equal(i <= 3 ? 1 : 0, venue.Placements);
                if (i <= 3) Assert.Equal(i, result.StagePlacementsUsed);
            }
            finally { File.Delete(Path.Combine(directory, "owner-run.lock")); Directory.Delete(directory); }
        }
        var snapshot = await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(RecoveryStore.Scope(123, "main"));
        Assert.Equal(6, snapshot.Commands.Count); Assert.Equal(6, snapshot.Receipts.Count);
    }

    [Theory]
    [InlineData("normal", "OMS_SCENARIO_PASS", 1, false)]
    [InlineData("lost-place", "OMS_SCENARIO_UNRESOLVED", 0, false)]
    [InlineData("lost-cancel", "OMS_SCENARIO_UNRESOLVED", 1, false)]
    [InlineData("fill-placement", "OMS_SCENARIO_STOPPED_FILL", 0, true)]
    [InlineData("fill-before-cancel", "OMS_SCENARIO_STOPPED_FILL", 0, true)]
    [InlineData("fill-cancel", "OMS_SCENARIO_STOPPED_FILL", 1, true)]
    [InlineData("late-fill", "OMS_SCENARIO_STOPPED_FILL", 1, true)]
    [InlineData("position-changed", "OMS_SCENARIO_UNRESOLVED", 1, false)]
    public async Task ActualJournalCaptureReceiptsAndRecoveryUseOneBoundedOwnerScenario(string scenario, string status, int cancels, bool fill)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        await using var capture = await TradeOps.DeribitLiveInboxQualification.LiveInboxQualification.CaptureRole.CreateAsync(db, CancellationToken.None);
        var socket = new Socket(); var venue = new Venue(socket, scenario); using var http = new HttpClient(venue);
        var directory = Path.Combine(Path.GetTempPath(), "tradeops-bounded-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await BoundedOmsScenario.RunAsync(Input(), http, directory, timeout.Token,
                _ => Task.FromResult(new BoundedOmsStore(db.Source, capture.Source, db.Source, false, new Noop())),
                options => Stream(() => socket, options));
            Assert.Equal(status, result.Status); Assert.Equal(1, venue.Placements); Assert.Equal(cancels, venue.Cancellations);
            Assert.Equal(fill, result.UnexpectedFill); Assert.False(result.PersistentStore);
            Assert.False(result.AccountReady); Assert.False(result.TradingReady); Assert.False(result.AutomaticFlatten);
            var serialized = JsonSerializer.Serialize(result);
            Assert.DoesNotContain("synthetic-secret", serialized); Assert.DoesNotContain("bounded-native-order", serialized);
            if (scenario == "normal")
            {
                Assert.True(result.CurrentCommandsValidated); Assert.True(result.AssessmentCompleted);
                Assert.Equal(2, result.ReceiptsRecorded); Assert.True(result.OpenEventCommitted); Assert.True(result.CancelEventCommitted);
                Assert.True(result.BaselinePositionPreserved); Assert.False(result.OwnOrderMayRemain); Assert.Equal(1, result.StagePlacementsUsed);
                Assert.Contains(nameof(DeribitRecoveryEvidenceBlocker.ExecutionLedgerCoverageUnproven), result.RecoveryBlockers);
                var scope = RecoveryStore.Scope(123, "main"); var ledger = await new OmsPostgresRecoveryLedgerReader(db.Source).ReadAsync(scope);
                Assert.True(ledger.IsUsable); Assert.Equal(2, ledger.Commands.Count); Assert.Equal(2, ledger.Receipts.Count);
                Assert.Equal(1, DeribitBoundedQualificationPolicy.ValidateAdmission(ledger, scope, DeribitBoundedQualificationPolicy.CurrentStage));
            }
            if (scenario == "lost-place") Assert.Equal(0, result.ReceiptsRecorded);
            if (fill) Assert.NotEqual("OMS_SCENARIO_PASS", result.Status);
        }
        finally { if (Directory.Exists(directory)) { File.Delete(Path.Combine(directory, "owner-run.lock")); Directory.Delete(directory); } }
    }

    [Fact]
    public async Task InvalidInputHasNoFilesystemStoreOrNetworkSideEffects()
    {
        var input = Input(); input.OwnerLaunched = false;
        var socket = new Socket(); var venue = new Venue(socket, "normal"); using var http = new HttpClient(venue);
        var directory = Path.Combine(Path.GetTempPath(), "tradeops-no-create-" + Guid.NewGuid().ToString("N")); var stores = 0;
        var result = await BoundedOmsScenario.RunAsync(input, http, directory, CancellationToken.None,
            _ => { stores++; throw new InvalidOperationException(); });
        Assert.Equal("OMS_SCENARIO_REFUSED", result.Status); Assert.Equal(0, stores); Assert.Equal(0, venue.Calls);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task MissingQualifiedStoreRefusesBeforeAuthenticationWithoutMigrating()
    {
        var socket = new Socket(); var venue = new Venue(socket, "normal"); using var http = new HttpClient(venue);
        var directory = Path.Combine(Path.GetTempPath(), "tradeops-store-refused-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await BoundedOmsScenario.RunAsync(Input(), http, directory, CancellationToken.None,
                _ => throw new InvalidOperationException("fixture store not provisioned"));
            Assert.Equal("OMS_SCENARIO_REFUSED", result.Status); Assert.Equal("Store", result.Stage);
            Assert.Equal(0, venue.Calls); Assert.Equal(0, result.PlacementAttempts); Assert.Null(result.StagePlacementsUsed);
        }
        finally { File.Delete(Path.Combine(directory, "owner-run.lock")); Directory.Delete(directory); }
    }

    internal static BoundedOmsScenario.Input Input() => new() { Purpose = "OWNER_BOUNDED_MAINNET_OMS_V1", Environment = "Mainnet",
        OwnerLaunched = true, StageId = DeribitBoundedQualificationPolicy.CurrentStage, ExpectedAccountId = 123, AccountType = "main",
        ClientId = "synthetic-client", ClientSecret = "synthetic-secret" };
    private sealed class Noop : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }

    private sealed class Venue(Socket socket, string scenario, Dictionary<string, JsonElement>? sharedHistory = null) : HttpMessageHandler
    {
        internal int Placements, Cancellations, Calls;
        private readonly string nativeId = "bounded-native-order-" + Guid.NewGuid().ToString("N");
        private readonly Dictionary<string, JsonElement> history = sharedHistory ?? new();
        private int positionReads; private string label = ""; private decimal price; private string state = "open";
        private decimal filled; private long stamp;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("www.deribit.com", request.RequestUri!.Host); Assert.Equal(HttpMethod.Post, request.Method); Calls++;
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = doc.RootElement; var p = root.GetProperty("params"); var method = root.GetProperty("method").GetString(); object result;
            switch (method)
            {
                case "public/auth":
                    var scope = p.GetProperty("scope").GetString()!;
                    if (scenario == "late-fill" && scope.Contains("session:recovery_", StringComparison.Ordinal))
                    { filled = .5m; stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); Emit(); }
                    result = new { access_token = "synthetic-token", expires_in = 300, scope }; break;
                case "public/get_instrument": result = DeribitBoundedOmsPolicyTests.Metadata(); break;
                case "public/get_order_book": result = DeribitBoundedOmsPolicyTests.Book(); break;
                case "public/get_time":
                    if (scenario == "quote-expired") await Task.Delay(TimeSpan.FromSeconds(6), ct);
                    result = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); break;
                case "private/get_account_summary": result = new { id = 123, type = "main", currency = "ETH", total_equity_usd = 8.9m, total_margin_balance_usd = 8.9m, total_initial_margin_usd = .1m }; break;
                case "private/get_open_orders_by_currency": result = Array.Empty<object>(); break;
                case "private/get_positions":
                    positionReads++; result = new[] { new { user_id = 123, instrument_name = "ETH-PERPETUAL", direction = "sell", size = scenario == "position-changed" && positionReads > 1 ? 11m : 10m } }; break;
                case "private/buy":
                    Placements++; Assert.Equal(1, Placements); Assert.Equal(1, p.GetProperty("amount").GetDecimal());
                    Assert.True(p.GetProperty("post_only").GetBoolean()); Assert.True(p.GetProperty("reject_post_only").GetBoolean());
                    price = p.GetProperty("price").GetDecimal(); Assert.True(price <= 2500.03m * .9m);
                    label = p.GetProperty("label").GetString()!; stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (scenario == "fill-placement") { state = "filled"; filled = 1; }
                    Emit(); if (scenario == "lost-place") throw new HttpRequestException("fixture lost placement reply");
                    result = new { order = Order(), trades = Array.Empty<object>() }; break;
                case "private/cancel":
                    Cancellations++; Assert.Equal(1, Cancellations); Assert.Equal(nativeId, p.GetProperty("order_id").GetString());
                    await Task.Delay(2, ct); state = "cancelled"; stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (scenario == "fill-cancel") filled = .25m;
                    Emit(); if (scenario == "lost-cancel") throw new HttpRequestException("fixture lost cancellation reply");
                    result = Order(); break;
                case "private/get_order_state":
                    var requested = p.GetProperty("order_id").GetString()!;
                    if (requested != nativeId) { result = history[requested]; break; }
                    if (scenario == "fill-before-cancel" && Cancellations == 0) { filled = .25m; stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); Emit(); }
                    result = Order(); break;
                case "private/get_user_trades_by_order": result = Array.Empty<object>(); break;
                case "private/get_open_orders_by_instrument": result = Array.Empty<object>(); break;
                case "private/get_order_history_by_instrument": result = history.Values.ToArray(); break;
                case "private/get_user_trades_by_instrument": result = new { trades = Array.Empty<object>(), has_more = false }; break;
                default: throw new InvalidOperationException("Unexpected or unbounded method.");
            }
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(new {
                jsonrpc = "2.0", id = root.GetProperty("id").GetInt64(), result }), Encoding.UTF8, "application/json") };
        }
        private object Order() => new { instrument_name = "ETH-PERPETUAL", order_id = nativeId, direction = "buy",
            amount = 1m, filled_amount = filled, price, order_state = state, last_update_timestamp = stamp, label,
            average_price = 0m, order_type = "limit", time_in_force = "good_til_cancelled", post_only = true, reduce_only = false };
        private void Emit()
        {
            history[nativeId] = JsonSerializer.SerializeToElement(Order());
            socket.Emit(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "subscription",
                @params = new { channel = "user.orders.future.ETH.raw", data = Order() } }));
        }
    }
}
