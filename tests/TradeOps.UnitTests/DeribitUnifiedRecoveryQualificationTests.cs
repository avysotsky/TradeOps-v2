using System.Net;
using System.Text;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using TradeOps.DeribitMainnetEthQualification;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitUnifiedRecoveryQualificationTests
{
    internal static Dictionary<string, object?> Input() => new() {
        ["Purpose"] = "PINNED_ACCOUNT_RECOVERY_ASSESSMENT_V1", ["Environment"] = "Mainnet", ["ClientId"] = "fixture-id",
        ["ClientSecret"] = "fixture-secret", ["ExpectedAccountId"] = 123, ["AccountType"] = "main",
        ["FromUtc"] = DateTimeOffset.UtcNow.AddDays(-1), ["ThroughUtc"] = DateTimeOffset.UtcNow.AddSeconds(-5) };
    internal static MemoryStream Stdin(Dictionary<string, object?>? input = null) => new(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input ?? Input())));
    internal sealed class Store : IOmsPrivateRecoveryReader, IAsyncDisposable
    {
        internal int Opens, Reads, Disposals; internal string Mode = "valid";
        internal Task<AccountRecoveryQualification.ReaderLease> Open(AccountKey scope, CancellationToken ct)
        {
            Opens++; if (Mode == "open-error") throw new IOException("never-output-private-error");
            return Task.FromResult(new AccountRecoveryQualification.ReaderLease(this, this));
        }
        public Task<OmsPrivateRecoveryReadResult> ReadAsync(AccountKey scope, int maximumRows = 10000, CancellationToken cancellationToken = default)
        {
            Reads++; if (Mode == "read-error") throw new IOException("never-output-private-error");
            return Task.FromResult(new OmsPrivateRecoveryReadResult(scope, Guid.NewGuid(), DateTimeOffset.UtcNow, 0,
                Array.Empty<OmsPrivateRecoveryObservation>(), Mode == "incident" ? new[] { OmsPrivateRecoveryReadProblem.IntegrityIncident } : Array.Empty<OmsPrivateRecoveryReadProblem>()));
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task AllProductionQueryPortsComposeIntoCompletedBlockedDiagnostics()
    {
        using var venue = new Venue(); using var http = new HttpClient(venue); using var stdin = Stdin(); var store = new Store();
        var result = await AccountRecoveryQualification.CollectAsync(stdin, http, store.Open, CancellationToken.None);
        Assert.Equal("RECOVERY_ACCOUNT_EVIDENCE_BLOCKED", result.Status); Assert.True(result.AssessmentCompleted);
        Assert.True(result.DatabaseUsed); Assert.True(result.SelectOnly); Assert.True(result.MetadataValidated);
        Assert.True(result.ScopedAccountObservationsValidated); Assert.Equal(1, result.AcceptedPositionRows); Assert.True(result.BalanceObserved);
        Assert.Equal(2, result.RequestedInstruments); Assert.Equal(1, store.Opens); Assert.Equal(1, store.Reads); Assert.Equal(1, store.Disposals);
        Assert.Equal(13, venue.Methods.Count); Assert.Equal(2, venue.Methods.Count(x => x == "public/auth"));
        Assert.Equal(0, venue.KnownLookups); AssertSafe(result);
    }

    [Theory]
    [InlineData("unmapped")] [InlineData("account")] [InlineData("grant")] [InlineData("query-http")]
    public async Task IncompleteSourcesStayVisibleAsBlockersWithoutPartialAccountOutput(string mode)
    {
        using var venue = new Venue { Mode = mode }; using var http = new HttpClient(venue); using var stdin = Stdin(); var store = new Store();
        var result = await AccountRecoveryQualification.CollectAsync(stdin, http, store.Open, CancellationToken.None);
        Assert.True(result.AssessmentCompleted); Assert.Equal("RECOVERY_ACCOUNT_EVIDENCE_BLOCKED", result.Status);
        Assert.False(result.ScopedAccountObservationsValidated); Assert.Equal(0, result.AcceptedPositionRows); Assert.False(result.BalanceObserved);
        Assert.Contains("AccountObservationsUnavailable", result.Blockers); Assert.NotEmpty(result.AccountProblems);
        Assert.Equal(1, store.Disposals); Assert.Equal(0, venue.KnownLookups); AssertSafe(result);
    }

    [Theory]
    [InlineData("purpose")] [InlineData("environment")] [InlineData("id")] [InlineData("secret")]
    [InlineData("large-secret")] [InlineData("pin")] [InlineData("type")] [InlineData("future")]
    [InlineData("interval")] [InlineData("offset")] [InlineData("unknown")] [InlineData("json")] [InlineData("oversize")]
    public async Task InvalidPurposePinCredentialsOrWindowRefusesBeforeNetworkAndStore(string mode)
    {
        var input = Input();
        switch (mode)
        {
            case "purpose": input["Purpose"] = "MAINNET_SMALL_ETH_ONE_ORDER_V1"; break;
            case "environment": input["Environment"] = "Testnet"; break;
            case "id": input["ClientId"] = null; break;
            case "secret": input["ClientSecret"] = "fixture\nsecret"; break;
            case "large-secret": input["ClientSecret"] = new string('x', 4097); break;
            case "pin": input["ExpectedAccountId"] = 0; break;
            case "type": input["AccountType"] = "unknown"; break;
            case "future": input["ThroughUtc"] = DateTimeOffset.UtcNow.AddDays(1); break;
            case "interval": input["FromUtc"] = DateTimeOffset.UtcNow.AddDays(-8); break;
            case "offset": input["FromUtc"] = DateTimeOffset.UtcNow.AddDays(-1).ToOffset(TimeSpan.FromHours(2)); break;
            case "unknown": input["PrivateField"] = "never-output-private-error"; break;
        }
        using var stdin = mode is "json" or "oversize" ? new MemoryStream(Encoding.UTF8.GetBytes(mode == "json" ? "{private" : new string('x', 16385))) : Stdin(input);
        using var venue = new Venue(); using var http = new HttpClient(venue); var store = new Store();
        var result = await AccountRecoveryQualification.CollectAsync(stdin, http, store.Open, CancellationToken.None);
        Assert.Equal("RECOVERY_ACCOUNT_ASSESSMENT_REFUSED", result.Status); Assert.Equal("Input", result.Stage);
        Assert.False(result.DatabaseUsed); Assert.Equal(0, store.Opens); Assert.Empty(venue.Methods); AssertSafe(result);
    }

    [Theory]
    [InlineData("inactive")] [InlineData("linear")]
    public async Task UnsupportedManifestRefusesBeforeStoreAndCredentials(string mode)
    {
        using var venue = new Venue { Mode = mode }; using var http = new HttpClient(venue); using var stdin = Stdin(); var store = new Store();
        var result = await AccountRecoveryQualification.CollectAsync(stdin, http, store.Open, CancellationToken.None);
        Assert.Equal("RECOVERY_ACCOUNT_ASSESSMENT_REFUSED", result.Status); Assert.Equal("Metadata", result.Stage);
        Assert.Equal(0, store.Opens); Assert.Single(venue.Methods); Assert.False(result.MetadataValidated); AssertSafe(result);
    }

    [Theory]
    [InlineData("open-error", "Store")] [InlineData("read-error", "Assessment")]
    public async Task StoreRefusalOrExceptionNeverLeaksDetails(string mode, string stage)
    {
        using var venue = new Venue(); using var http = new HttpClient(venue); using var stdin = Stdin(); var store = new Store { Mode = mode };
        var result = await AccountRecoveryQualification.CollectAsync(stdin, http, store.Open, CancellationToken.None);
        Assert.Equal("RECOVERY_ACCOUNT_ASSESSMENT_REFUSED", result.Status); Assert.Equal(stage, result.Stage);
        Assert.True(result.DatabaseUsed); Assert.Equal(mode == "read-error", result.SelectOnly);
        Assert.Single(venue.Methods); Assert.Equal(mode == "read-error" ? 1 : 0, store.Disposals); AssertSafe(result);
    }

    [Fact]
    public async Task RetainedIncidentSkipsAllPrivateReadsAndDisposesLease()
    {
        using var venue = new Venue(); using var http = new HttpClient(venue); using var stdin = Stdin(); var store = new Store { Mode = "incident" };
        var result = await AccountRecoveryQualification.CollectAsync(stdin, http, store.Open, CancellationToken.None);
        Assert.True(result.AssessmentCompleted); Assert.Contains("SnapshotUnusable", result.Blockers); Assert.Contains("ReadSkipped", result.AccountProblems);
        Assert.False(result.ScopedAccountObservationsValidated); Assert.Equal(1, store.Disposals); Assert.Single(venue.Methods); AssertSafe(result);
    }

    [Fact]
    public async Task CancelledInputCannotOpenStoreOrConnect()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel(); using var venue = new Venue(); using var http = new HttpClient(venue);
        using var stdin = Stdin(); var store = new Store();
        var result = await AccountRecoveryQualification.CollectAsync(stdin, http, store.Open, stop.Token);
        Assert.False(result.AssessmentCompleted); Assert.Equal(0, store.Opens); Assert.Empty(venue.Methods); AssertSafe(result);
    }

    internal static void AssertSafe(AccountRecoveryQualification.Diagnostics result)
    {
        Assert.False(result.AccountWideCoverage); Assert.False(result.VenueCoherent); Assert.False(result.AccountReady);
        Assert.False(result.DurableProjectionPublished); Assert.False(result.TradingReady); Assert.Equal(0, result.DurableWrites); Assert.Equal(0, result.MutatingCalls);
        if (result.AssessmentCompleted)
            foreach (var blocker in new[] { "CoherentCutUnavailable", "BufferedReplayUnavailable", "ExecutionLedgerCoverageUnproven", "AccountSnapshotCoherenceUnproven", "AccountWideCoverageUnproven" })
                Assert.Contains(blocker, result.Blockers);
        var json = JsonSerializer.Serialize(result);
        foreach (var value in new[] { "fixture-secret", "never-output-private-error", "ETH-27NOV26", "6543.210987", "9876.54321", "CashBalance", "NativeSize", "observed-native:", "ExchangeOrderId" })
            Assert.DoesNotContain(value, json);
    }

    internal sealed class Venue : HttpMessageHandler
    {
        internal string Mode = "valid"; internal readonly List<string> Methods = new(); internal int KnownLookups; private int auths;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("www.deribit.com", request.RequestUri!.Host); var method = request.RequestUri.AbsolutePath.Replace("/api/v2/", "");
            Methods.Add(method); object result; long id = 1;
            if (method == "public/get_instruments")
            {
                Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Headers.Authorization);
                Assert.Equal("?currency=ETH&kind=future&expired=false", request.RequestUri.Query);
                result = new[] { Metadata("ETH-PERPETUAL"), Metadata("ETH-27NOV26") };
            }
            else
            {
                Assert.Equal(HttpMethod.Post, request.Method); Assert.Empty(request.RequestUri.Query);
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var root = document.RootElement; id = root.GetProperty("id").GetInt64(); var p = root.GetProperty("params");
                if (method == "public/auth")
                {
                    auths++; Assert.Null(request.Headers.Authorization); var scope = p.GetProperty("scope").GetString()!;
                    Assert.DoesNotContain("read_write", scope); result = new { access_token = "fixture-token", scope = Mode == "grant" ? scope.Replace("trade:read", "trade:read_write") : scope, expires_in = 300 };
                }
                else
                {
                    Assert.Equal("fixture-token", request.Headers.Authorization?.Parameter);
                    if (Mode == "query-http") return new(HttpStatusCode.TooManyRequests) { RequestMessage = request };
                    if (method == "private/get_account_summary")
                    {
                        Assert.Equal("ETH", p.GetProperty("currency").GetString()); Assert.True(p.GetProperty("extended").GetBoolean()); Assert.Equal(2, p.EnumerateObject().Count());
                        result = new { id = Mode == "account" ? 124 : 123, type = "main", currency = "ETH", balance = 6543.210987m,
                            equity = 9876.54321m, cross_collateral_enabled = false, portfolio_margining_enabled = false };
                    }
                    else if (method == "private/get_positions")
                    {
                        Assert.Equal(2, auths); Assert.Equal("ETH", p.GetProperty("currency").GetString()); Assert.Equal("future", p.GetProperty("kind").GetString()); Assert.Equal(2, p.EnumerateObject().Count());
                        result = new[] { new { instrument_name = Mode == "unmapped" ? "OTHER" : "ETH-27NOV26", kind = "future", user_id = 123, direction = "sell", size = -10, size_currency = -0.005m } };
                    }
                    else
                    {
                        if (method == "private/get_order_state") KnownLookups++;
                        Assert.Equal("ETH-PERPETUAL", p.GetProperty("instrument_name").GetString());
                        switch (method)
                        {
                            case "private/get_open_orders_by_instrument": result = Array.Empty<object>(); break;
                            case "private/get_order_history_by_instrument":
                                Assert.True(p.GetProperty("include_old").GetBoolean()); Assert.Equal(!p.GetProperty("historical").GetBoolean(), p.GetProperty("include_unfilled").GetBoolean());
                                result = Array.Empty<object>(); break;
                            default:
                                Assert.Equal("private/get_user_trades_by_instrument", method);
                                Assert.Equal("asc", p.GetProperty("sorting").GetString()); result = new { trades = Array.Empty<object>(), has_more = false }; break;
                        }
                    }
                }
            }
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result })) };
        }
        private object Metadata(string name) => new { instrument_name = name, kind = "future", instrument_type = Mode == "linear" ? "linear" : "reversed",
            base_currency = "ETH", quote_currency = "USD", settlement_currency = "ETH", is_active = Mode != "inactive" };
    }
}
