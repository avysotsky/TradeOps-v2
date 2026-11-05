using System.Net;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitAccountSnapshotTests
{
    private static DeribitAccountSnapshotRequest Request(string kind = "future") => new(Account, Epoch, "ETH", kind, TimeSpan.FromSeconds(10));
    private static DeribitAccountSnapshotReader Reader(HttpClient http, Clock clock, string kind = "future", bool linear = false) => new(new(Account,
        new() { ClientId = "fixture-id", ClientSecret = "fixture-secret" }, new(123, false,
            new[] { new DeribitPrivateInstrument("ETH-PERPETUAL", kind, "ETH",
                kind == "option" || linear ? OmsPrivateAmountUnit.BaseCoin : OmsPrivateAmountUnit.Usd,
                kind == "option" || linear ? "ETH" : "USD") })), http, clock);

    [Theory]
    [InlineData("future", "buy", 10)] [InlineData("future", "sell", -10)] [InlineData("future", "zero", 0)]
    [InlineData("option", "buy", 2)] [InlineData("option", "sell", -2)]
    public async Task RetainsNativeUnitsAndObservationBoundsWithoutRecoveryAuthority(string kind, string direction, decimal size)
    {
        var clock = new Clock(); using var venue = new Venue(clock) { Kind = kind, Direction = direction, Size = size };
        using var http = new HttpClient(venue); var q = Request(kind); var result = await Reader(http, clock, kind).ReadAsync(q);
        Assert.True(result.QueriesCompleted); Assert.True(result.IsFreshAt(clock.Now));
        Assert.False(result.IsFreshAt(clock.Now.AddSeconds(11))); Assert.False(result.IsFreshAt(clock.Now.AddSeconds(-1)));
        Assert.Equal(q.Scope, result.Scope); Assert.Equal(q.Epoch, result.Epoch); Assert.Equal("ETH", result.Currency);
        Assert.Equal("ETH-PERPETUAL", Assert.Single(result.RequestedInstruments));
        var position = Assert.Single(result.Positions); Assert.Equal(size, position.NativeSize);
        Assert.Equal(kind == "future" ? "USD" : "ETH", position.SizeCurrency);
        Assert.Equal(kind == "future" ? size / 2000 : (decimal?)null, position.BaseSize);
        Assert.Equal("ETH", position.BaseCurrency); Assert.Equal(direction, position.Direction);
        Assert.Equal(-0.25m, result.Balance!.CashBalance); Assert.Equal(0.5m, result.Balance.Equity);
        Assert.True(result.Balance.CrossCollateralEnabled); Assert.True(result.Balance.PortfolioMarginingEnabled);
        Assert.True(result.Balance.Window.RequestedAtUtc <= result.Balance.Window.ReceivedAtUtc);
        Assert.True(result.PositionsWindow!.RequestedAtUtc >= result.Balance.Window.ReceivedAtUtc);
        Assert.False(result.AccountWideCoverage); Assert.False(result.VenueCoherent); Assert.False(result.AccountReady);
        Assert.False(result.DurableProjectionPublished); Assert.Equal(4, venue.Calls);
    }

    [Fact]
    public async Task EmptyScopedResultDoesNotInventZeroRowsOrAccountWideCoverage()
    {
        var clock = new Clock(); using var venue = new Venue(clock) { Mode = "empty" }; using var http = new HttpClient(venue);
        var result = await Reader(http, clock).ReadAsync(Request());
        Assert.True(result.QueriesCompleted); Assert.Empty(result.Positions); Assert.Single(result.RequestedInstruments);
        Assert.False(result.AccountWideCoverage); Assert.Equal(4, venue.Calls);
    }

    [Theory]
    [InlineData("account", DeribitAccountSnapshotProblem.AccountMismatch)]
    [InlineData("changed-account", DeribitAccountSnapshotProblem.AccountMismatch)]
    [InlineData("type", DeribitAccountSnapshotProblem.AccountMismatch)]
    [InlineData("currency", DeribitAccountSnapshotProblem.AccountMismatch)]
    [InlineData("user", DeribitAccountSnapshotProblem.AccountMismatch)]
    [InlineData("grant", DeribitAccountSnapshotProblem.Authentication)]
    [InlineData("expiry", DeribitAccountSnapshotProblem.Authentication)]
    [InlineData("token", DeribitAccountSnapshotProblem.Authentication)]
    [InlineData("kind", DeribitAccountSnapshotProblem.Protocol)]
    [InlineData("instrument", DeribitAccountSnapshotProblem.UnmappedInstrument)]
    [InlineData("duplicate", DeribitAccountSnapshotProblem.Protocol)]
    [InlineData("size", DeribitAccountSnapshotProblem.Protocol)]
    [InlineData("base", DeribitAccountSnapshotProblem.Protocol)]
    [InlineData("direction", DeribitAccountSnapshotProblem.Protocol)]
    [InlineData("missing-balance", DeribitAccountSnapshotProblem.Protocol)]
    [InlineData("missing-flag", DeribitAccountSnapshotProblem.Protocol)]
    [InlineData("missing-base", DeribitAccountSnapshotProblem.Protocol)]
    [InlineData("rpc-id", DeribitAccountSnapshotProblem.Protocol)]
    [InlineData("http", DeribitAccountSnapshotProblem.Transport)]
    [InlineData("deadline", DeribitAccountSnapshotProblem.Deadline)]
    [InlineData("overflow", DeribitAccountSnapshotProblem.Protocol)]
    [InlineData("rowcap", DeribitAccountSnapshotProblem.Capacity)]
    [InlineData("null", DeribitAccountSnapshotProblem.Protocol)]
    [InlineData("late", DeribitAccountSnapshotProblem.StaleObservation)]
    [InlineData("backwards", DeribitAccountSnapshotProblem.StaleObservation)]
    public async Task RefusedOrIncompleteReadClearsAllAccountValues(string mode, DeribitAccountSnapshotProblem problem)
    {
        var clock = new Clock(); using var venue = new Venue(clock) { Mode = mode }; using var http = new HttpClient(venue);
        var result = await Reader(http, clock).ReadAsync(Request() with { MaximumPositions = mode == "rowcap" ? 1 : 256 });
        Assert.False(result.QueriesCompleted); Assert.False(result.IsFreshAt(clock.Now)); Assert.Contains(problem, result.Problems);
        Assert.Null(result.Balance); Assert.Null(result.PositionsWindow); Assert.Empty(result.Positions);
        Assert.InRange(venue.Calls, 1, 4); // No fallback, retry, trading method or persistent write.
    }

    [Theory]
    [InlineData("scope")] [InlineData("epoch")] [InlineData("currency")] [InlineData("kind")]
    [InlineData("capacity")] [InlineData("min-span")] [InlineData("max-span")] [InlineData("linear")]
    [InlineData("auth-header")] [InlineData("cookie")]
    public async Task InvalidBindingMakesNoNetworkCall(string mode)
    {
        var clock = new Clock(); using var venue = new Venue(clock); using var http = new HttpClient(venue); var q = Request();
        q = mode switch {
            "scope" => q with { Scope = new(new("foreign"), Account.Venue, Account.Environment, Account.Account) },
            "epoch" => q with { Epoch = Guid.Empty }, "currency" => q with { Currency = "any" },
            "kind" => q with { Kind = "future_combo" }, "capacity" => q with { MaximumPositions = 4097 },
            "min-span" => q with { MaximumObservationSpan = TimeSpan.Zero },
            "max-span" => q with { MaximumObservationSpan = TimeSpan.FromMinutes(2) }, _ => q };
        if (mode == "auth-header") http.DefaultRequestHeaders.Authorization = new("Bearer", "foreign");
        if (mode == "cookie") http.DefaultRequestHeaders.Add("Cookie", "fixture");
        await Assert.ThrowsAsync<ArgumentException>(() => Reader(http, clock, linear: mode == "linear").ReadAsync(q));
        Assert.Equal(0, venue.Calls);
    }

    [Fact]
    public async Task CancellationPropagatesBeforeNetworkCall()
    {
        var clock = new Clock(); using var venue = new Venue(clock); using var http = new HttpClient(venue);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(http, clock).ReadAsync(Request(), stop.Token));
        Assert.Equal(0, venue.Calls);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Venue(Clock clock) : HttpMessageHandler
    {
        internal int Calls; internal string Mode = "valid", Kind = "future", Direction = "buy"; internal decimal Size = 10;
        private int summaries;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; if (Mode == "deadline") throw new OperationCanceledException();
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("test.deribit.com", request.RequestUri!.Host);
            Assert.Empty(request.RequestUri.Query); Assert.True(request.Headers.CacheControl!.NoStore);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = document.RootElement; var method = root.GetProperty("method").GetString()!; var p = root.GetProperty("params");
            object? result;
            if (method == "public/auth")
            {
                Assert.Null(request.Headers.Authorization); var scope = p.GetProperty("scope").GetString()!;
                Assert.DoesNotContain("read_write", scope);
                result = new { access_token = Mode == "token" ? "" : "fixture-token", expires_in = Mode == "expiry" ? 900 : 300,
                    scope = Mode == "grant" ? scope.Replace("trade:read", "trade:read_write") : scope };
            }
            else
            {
                Assert.Equal("fixture-token", request.Headers.Authorization?.Parameter);
                Assert.Equal("ETH", p.GetProperty("currency").GetString()); Assert.Equal(2, p.EnumerateObject().Count());
                if (method == "private/get_account_summary")
                {
                    Assert.True(p.GetProperty("extended").GetBoolean()); summaries++;
                    var fields = new Dictionary<string, object> {
                        ["id"] = Mode == "account" || Mode == "changed-account" && summaries == 2 ? 124 : 123,
                        ["type"] = Mode == "type" ? "subaccount" : "main", ["currency"] = Mode == "currency" ? "BTC" : "ETH",
                        ["balance"] = -0.25m, ["equity"] = 0.5m, ["cross_collateral_enabled"] = true, ["portfolio_margining_enabled"] = true };
                    if (Mode == "missing-balance") fields.Remove("balance"); if (Mode == "missing-flag") fields.Remove("cross_collateral_enabled");
                    result = fields;
                }
                else
                {
                    Assert.Equal("private/get_positions", method); Assert.Equal(Kind, p.GetProperty("kind").GetString());
                    var row = new Dictionary<string, object> {
                        ["instrument_name"] = "ETH-PERPETUAL", ["kind"] = Mode == "kind" ? "option" : Kind,
                        ["user_id"] = Mode == "user" ? 124 : 123, ["direction"] = Mode == "direction" ? "unknown" : Direction,
                        ["size"] = Mode == "size" ? "10" : Mode == "overflow" ? 1e100 : Size,
                        ["size_currency"] = Mode == "base" ? -10 : Size / 2000 };
                    if (Mode == "missing-base") row.Remove("size_currency");
                    var rows = new List<object> { row };
                    if (Mode is "duplicate" or "rowcap") rows.Add(row);
                    if (Mode == "instrument") rows.Add(new { instrument_name = "UNMAPPED" });
                    if (Mode == "empty") rows.Clear();
                    if (Mode == "late") clock.Now = clock.Now.AddSeconds(11);
                    if (Mode == "backwards") clock.Now = clock.Now.AddSeconds(-1);
                    result = Mode == "null" ? null : rows;
                }
            }
            var id = root.GetProperty("id").GetInt64() + (Mode == "rpc-id" ? 1 : 0);
            return new(Mode == "http" ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK) {
                RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result })) };
        }
    }
}
