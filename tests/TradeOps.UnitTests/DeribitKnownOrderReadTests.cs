using System.Net;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitKnownOrderReadTests
{
    private static OmsPrivateRecoveryReadResult Snapshot() => new(Account, Guid.NewGuid(), DateTimeOffset.UtcNow, 2,
        new[] { new OmsPrivateRecoveryObservation(Prior(), OmsPrivateCaptureResult.Inserted), new(Report(), OmsPrivateCaptureResult.Inserted) },
        Array.Empty<OmsPrivateRecoveryReadProblem>());
    private static DeribitKnownOrderReadRequest Request() => DeribitKnownOrderSelection.FromSnapshot(Snapshot(), Account, Epoch, "ETH-PERPETUAL");
    private static DeribitKnownOrderReader Reader(HttpClient http) => new(new(Account,
        new() { ClientId = "fixture-id", ClientSecret = "fixture-secret" },
        new(123, false, new[] { new DeribitPrivateInstrument("ETH-PERPETUAL", "future", "ETH", OmsPrivateAmountUnit.Usd, "USD") })), http);

    [Fact]
    public void SelectLatestRetainedReport_DoesNotInventCancelledFromAbsence()
    {
        var snapshot = Snapshot(); var request = DeribitKnownOrderSelection.FromSnapshot(snapshot, Account, Epoch, "ETH-PERPETUAL");
        Assert.Equal(snapshot.SnapshotId, request.SnapshotId); Assert.Equal("cancelled", Assert.Single(request.Orders).OrderState);
        Assert.Equal(Account, request.Scope); Assert.Equal(Epoch, request.Epoch);
    }

    [Theory]
    [InlineData("problem")] [InlineData("scope")] [InlineData("embedded-scope")] [InlineData("disposition")]
    [InlineData("empty")] [InlineData("fill-only")] [InlineData("conflicting-terms")] [InlineData("snapshot")]
    public void InvalidRetainedEvidenceRefusesSelection(string mode)
    {
        var s = Snapshot();
        s = mode switch {
            "problem" => s with { Problems = new[] { OmsPrivateRecoveryReadProblem.IntegrityIncident } },
            "scope" => s with { Scope = new(new("foreign"), Account.Venue, Account.Environment, Account.Account) },
            "embedded-scope" => s with { Observations = new[] { new OmsPrivateRecoveryObservation(Report() with {
                Scope = new(new("foreign"), Account.Venue, Account.Environment, Account.Account) }, OmsPrivateCaptureResult.Inserted) } },
            "disposition" => s with { Observations = new[] { new OmsPrivateRecoveryObservation(Report(), OmsPrivateCaptureResult.Collision) } },
            "empty" => s with { Observations = Array.Empty<OmsPrivateRecoveryObservation>() },
            "fill-only" => s with { Observations = new[] { new OmsPrivateRecoveryObservation(Fill(), OmsPrivateCaptureResult.Inserted) } },
            "conflicting-terms" => s with { Observations = new[] { new OmsPrivateRecoveryObservation(Report(), OmsPrivateCaptureResult.Inserted),
                new OmsPrivateRecoveryObservation(Report(amount: 20, minute: 3), OmsPrivateCaptureResult.Inserted) } },
            _ => s with { SnapshotId = Guid.Empty } };
        Assert.Throws<InvalidOperationException>(() => DeribitKnownOrderSelection.FromSnapshot(s, Account, Epoch, "ETH-PERPETUAL"));
    }

    [Fact]
    public async Task FoundReportRetainsBindingAndCurrentProvenanceWithoutRecoveryAuthority()
    {
        using var venue = new Venue(); using var http = new HttpClient(venue);
        var request = Request(); var result = await Reader(http).ReadAsync(request);
        Assert.True(result.QueriesCompleted); Assert.True(result.AllOrdersFound); Assert.False(result.CoherentRecovery);
        var observation = Assert.Single(result.Observations);
        Assert.Equal(Account, observation.Scope); Assert.Equal(Epoch, observation.Observation.StreamEpoch);
        Assert.Equal(request.SnapshotId, result.SnapshotId); Assert.Equal(OmsPrivateCaptureSource.RecoveryQuery, observation.Observation.Source);
        Assert.Equal("private/get_order_state", observation.Observation.Channel); Assert.Equal(4, venue.Calls);
    }

    [Theory]
    [InlineData(10004, DeribitKnownOrderLookupOutcome.Missing)]
    [InlineData(10033, DeribitKnownOrderLookupOutcome.Unsupported)]
    [InlineData(-32601, DeribitKnownOrderLookupOutcome.Unsupported)]
    public async Task ExplicitMissingOrUnsupportedNeverInventsCancellation(int code, DeribitKnownOrderLookupOutcome outcome)
    {
        using var venue = new Venue { ErrorCode = code }; using var http = new HttpClient(venue);
        var result = await Reader(http).ReadAsync(Request());
        Assert.True(result.QueriesCompleted); Assert.False(result.AllOrdersFound); Assert.Empty(result.Observations);
        Assert.Equal(outcome, Assert.Single(result.Lookups).Outcome); Assert.Equal(code, result.Lookups[0].VenueCode);
        Assert.Equal(4, venue.Calls); // Auth, account, one addressed request, account; no retries/fallback.
    }

    [Fact]
    public async Task PartiallyMissingSetDoesNotExposePartialCurrentFacts()
    {
        using var venue = new Venue { Mode = "second-missing" }; using var http = new HttpClient(venue);
        var q = Request(); q = q with { Orders = new[] { q.Orders[0], q.Orders[0] with { ExchangeOrderId = "order-2" } } };
        var result = await Reader(http).ReadAsync(q);
        Assert.True(result.QueriesCompleted); Assert.False(result.AllOrdersFound); Assert.Empty(result.Observations);
        Assert.Equal(new[] { DeribitKnownOrderLookupOutcome.Found, DeribitKnownOrderLookupOutcome.Missing }, result.Lookups.Select(x => x.Outcome));
    }

    [Theory]
    [InlineData("account", DeribitKnownOrderReadProblem.AccountMismatch)]
    [InlineData("changed-account", DeribitKnownOrderReadProblem.AccountMismatch)]
    [InlineData("grant", DeribitKnownOrderReadProblem.Authentication)]
    [InlineData("id", DeribitKnownOrderReadProblem.EvidenceMismatch)]
    [InlineData("amount", DeribitKnownOrderReadProblem.EvidenceMismatch)]
    [InlineData("side", DeribitKnownOrderReadProblem.EvidenceMismatch)]
    [InlineData("stale", DeribitKnownOrderReadProblem.EvidenceMismatch)]
    [InlineData("future", DeribitKnownOrderReadProblem.EvidenceMismatch)]
    [InlineData("reopened", DeribitKnownOrderReadProblem.EvidenceMismatch)]
    [InlineData("missing-filled", DeribitKnownOrderReadProblem.EvidenceMismatch)]
    [InlineData("quote", DeribitKnownOrderReadProblem.EvidenceMismatch)]
    [InlineData("instrument", DeribitKnownOrderReadProblem.Protocol)]
    [InlineData("rpc-id", DeribitKnownOrderReadProblem.Protocol)]
    [InlineData("unknown-error", DeribitKnownOrderReadProblem.Protocol)]
    [InlineData("http", DeribitKnownOrderReadProblem.Transport)]
    [InlineData("deadline", DeribitKnownOrderReadProblem.Deadline)]
    public async Task MismatchedOrFailedReadsClearAllCurrentFacts(string mode, DeribitKnownOrderReadProblem problem)
    {
        using var venue = new Venue { Mode = mode }; using var http = new HttpClient(venue);
        var result = await Reader(http).ReadAsync(Request());
        Assert.False(result.QueriesCompleted); Assert.False(result.AllOrdersFound);
        Assert.Contains(problem, result.Problems); Assert.Empty(result.Observations);
    }

    [Theory]
    [InlineData("scope")] [InlineData("epoch")] [InlineData("snapshot")] [InlineData("duplicate")] [InlineData("capacity")]
    public async Task InvalidRequestsMakeNoNetworkCall(string mode)
    {
        using var venue = new Venue(); using var http = new HttpClient(venue); var q = Request();
        q = mode switch {
            "scope" => q with { Scope = new(new("foreign"), Account.Venue, Account.Environment, Account.Account) },
            "epoch" => q with { Epoch = Guid.Empty }, "snapshot" => q with { SnapshotId = Guid.Empty },
            "duplicate" => q with { Orders = new[] { q.Orders[0], q.Orders[0] } },
            _ => q with { Orders = Enumerable.Repeat(q.Orders[0], 33).ToArray() } };
        await Assert.ThrowsAsync<ArgumentException>(() => Reader(http).ReadAsync(q)); Assert.Equal(0, venue.Calls);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using var venue = new Venue(); using var http = new HttpClient(venue); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(http).ReadAsync(Request(), stop.Token)); Assert.Equal(0, venue.Calls);
    }

    private sealed class Venue : HttpMessageHandler
    {
        internal int Calls; internal string Mode = "found"; internal int? ErrorCode; private int accountCalls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; if (Mode == "deadline") throw new OperationCanceledException();
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("test.deribit.com", request.RequestUri!.Host); Assert.Equal("", request.RequestUri.Query);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = document.RootElement; var method = root.GetProperty("method").GetString()!; var p = root.GetProperty("params");
            object result; int? code = null;
            if (method == "public/auth")
            {
                Assert.Null(request.Headers.Authorization); var scope = p.GetProperty("scope").GetString()!; Assert.DoesNotContain("read_write", scope);
                result = new { access_token = "fixture-token", expires_in = 300, scope = Mode == "grant" ? scope.Replace("trade:read", "trade:read_write") : scope };
            }
            else
            {
                Assert.Equal("fixture-token", request.Headers.Authorization?.Parameter);
                if (method == "private/get_account_summary")
                    result = new { id = Mode == "account" || ++accountCalls == 2 && Mode == "changed-account" ? 124 : 123, type = "main", currency = "ETH" };
                else
                {
                    Assert.Equal("private/get_order_state", method); Assert.Single(p.EnumerateObject());
                    var orderId = p.GetProperty("order_id").GetString()!; code = ErrorCode;
                    if (Mode == "unknown-error") code = 10029;
                    if (Mode == "second-missing" && orderId == "order-2") code = 10004;
                    var fields = new Dictionary<string, object> {
                        ["order_id"] = Mode == "id" ? "foreign" : orderId,
                        ["instrument_name"] = Mode == "instrument" ? "OTHER" : "ETH-PERPETUAL",
                        ["direction"] = Mode == "side" ? "sell" : "buy", ["amount"] = Mode == "amount" ? 20 : 10,
                        ["order_state"] = Mode == "reopened" ? "open" : "cancelled", ["filled_amount"] = 0, ["price"] = 2000,
                        ["last_update_timestamp"] = (Mode == "future" ? DateTimeOffset.UtcNow.AddDays(1) : From.AddMinutes(Mode == "stale" ? 1 : 3)).ToUnixTimeMilliseconds() };
                    if (Mode == "missing-filled") fields.Remove("filled_amount"); if (Mode == "quote") fields["quote_id"] = "quote";
                    result = fields;
                }
            }
            if (Mode == "http") return new(HttpStatusCode.TooManyRequests) { RequestMessage = request };
            var id = root.GetProperty("id").GetInt64() + (Mode == "rpc-id" ? 1 : 0);
            var body = code.HasValue ? JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message = "never-output-private-error" } })
                : JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
            return new(code.HasValue ? HttpStatusCode.BadRequest : HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body) };
        }
    }
}
