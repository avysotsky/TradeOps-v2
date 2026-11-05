using System.Net;
using System.Text;
using System.Text.Json;
using TradeOps.DeribitMainnetEthQualification;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitAccountQualificationTests
{
    private static Dictionary<string, object?> Input() => new() {
        ["Purpose"] = "READ_ONLY_ETH_ACCOUNT_SNAPSHOT_V1", ["Environment"] = "Mainnet",
        ["ClientId"] = "fixture-private-client", ["ClientSecret"] = "fixture-private-secret",
        ["ExpectedAccountId"] = 654321, ["AccountType"] = "main" };
    private static MemoryStream Stdin(Dictionary<string, object?>? input = null) => new(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input ?? Input())));

    [Fact]
    public async Task ValidScopedReadEmitsOnlyAnonymousDiagnostics()
    {
        using var venue = new Venue(); using var http = new HttpClient(venue); using var stdin = Stdin();
        var result = await AccountSnapshotQualification.CollectAsync(stdin, http, CancellationToken.None);
        Assert.Equal("ACCOUNT_SNAPSHOT_SCOPED_PASS", result.Status); Assert.Equal("Completed", result.Stage);
        Assert.True(result.MetadataValidated); Assert.True(result.NativeBindingVerified); Assert.True(result.FreshAtAssessment);
        Assert.True(result.QueriesCompleted); Assert.Equal(1, result.PositionCount); Assert.True(result.BalanceObserved);
        Assert.Equal(new[] { "public/get_instruments", "public/auth", "private/get_account_summary", "private/get_positions", "private/get_account_summary" }, venue.Methods);
        AssertSafe(result);
    }

    [Fact]
    public async Task EmptyPositionsRemainScopedAndDoNotInventRows()
    {
        using var venue = new Venue { Mode = "empty" }; using var http = new HttpClient(venue); using var stdin = Stdin();
        var result = await AccountSnapshotQualification.CollectAsync(stdin, http, CancellationToken.None);
        Assert.Equal("ACCOUNT_SNAPSHOT_SCOPED_PASS", result.Status); Assert.Equal(0, result.PositionCount);
        Assert.True(result.BalanceObserved); AssertSafe(result);
    }

    [Fact]
    public async Task NativeSubaccountUsesPinnedIdentityWithoutAccountSelectionParameters()
    {
        using var venue = new Venue { Mode = "subaccount" }; using var http = new HttpClient(venue);
        var input = Input(); input["AccountType"] = "subaccount"; using var stdin = Stdin(input);
        var result = await AccountSnapshotQualification.CollectAsync(stdin, http, CancellationToken.None);
        Assert.Equal("ACCOUNT_SNAPSHOT_SCOPED_PASS", result.Status); Assert.True(result.NativeBindingVerified); AssertSafe(result);
    }

    [Theory]
    [InlineData("inactive")] [InlineData("base")] [InlineData("linear")] [InlineData("absent")]
    [InlineData("catalog-http")] [InlineData("catalog-json")]
    public async Task InvalidCatalogRefusesBeforeSendingCredentials(string mode)
    {
        using var venue = new Venue { Mode = mode }; using var http = new HttpClient(venue); using var stdin = Stdin();
        var result = await AccountSnapshotQualification.CollectAsync(stdin, http, CancellationToken.None);
        Assert.Equal("ACCOUNT_SNAPSHOT_REFUSED", result.Status); Assert.Equal("Metadata", result.Stage);
        Assert.False(result.MetadataValidated); Assert.False(result.QueriesCompleted); Assert.Single(venue.Methods);
        AssertSafe(result);
    }

    [Theory]
    [InlineData("changed-account", "AccountMismatch")] [InlineData("user", "AccountMismatch")]
    [InlineData("grant", "Authentication")] [InlineData("unmapped", "UnmappedInstrument")]
    [InlineData("protocol", "Protocol")] [InlineData("transport", "Transport")]
    [InlineData("stale", "StaleObservation")]
    public async Task IncompleteQueriesEmitNoAccountValuesOrRawErrors(string mode, string problem)
    {
        var clock = new Clock(); using var venue = new Venue { Mode = mode, Clock = clock };
        using var http = new HttpClient(venue); using var stdin = Stdin();
        var result = await AccountSnapshotQualification.CollectAsync(stdin, http, CancellationToken.None, clock);
        Assert.Equal("ACCOUNT_SNAPSHOT_INCOMPLETE", result.Status); Assert.Contains(problem, result.Problems);
        Assert.True(result.MetadataValidated); Assert.False(result.QueriesCompleted); Assert.False(result.NativeBindingVerified);
        Assert.False(result.FreshAtAssessment); Assert.False(result.BalanceObserved); Assert.Equal(0, result.PositionCount);
        Assert.InRange(venue.Methods.Count, 2, 5); AssertSafe(result);
    }

    [Theory]
    [InlineData("purpose")] [InlineData("environment")] [InlineData("id")] [InlineData("secret")]
    [InlineData("large-secret")] [InlineData("pin")] [InlineData("type")] [InlineData("unknown")]
    [InlineData("json")] [InlineData("oversize")]
    public async Task InvalidInputRefusesBeforeAnyNetworkRequest(string mode)
    {
        var input = Input();
        switch (mode)
        {
            case "purpose": input["Purpose"] = "MAINNET_SMALL_ETH_ONE_ORDER_V1"; break;
            case "environment": input["Environment"] = "Testnet"; break;
            case "id": input["ClientId"] = null; break;
            case "secret": input["ClientSecret"] = "secret\nprivate"; break;
            case "large-secret": input["ClientSecret"] = new string('x', 4097); break;
            case "pin": input["ExpectedAccountId"] = 0; break;
            case "type": input["AccountType"] = "other"; break;
            case "unknown": input["UnexpectedPrivateField"] = "never-output-private-error"; break;
        }
        using var stdin = mode is "json" or "oversize"
            ? new MemoryStream(Encoding.UTF8.GetBytes(mode == "json" ? "{never-output-private-error" : new string('x', 16385))) : Stdin(input);
        using var venue = new Venue(); using var http = new HttpClient(venue);
        var result = await AccountSnapshotQualification.CollectAsync(stdin, http, CancellationToken.None);
        Assert.Equal("ACCOUNT_SNAPSHOT_REFUSED", result.Status); Assert.Equal("Input", result.Stage);
        Assert.False(result.MetadataValidated); Assert.Empty(venue.Methods); AssertSafe(result);
    }

    [Fact]
    public async Task CancellationProducesOnlySanitizedRefusal()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        using var venue = new Venue(); using var http = new HttpClient(venue); using var stdin = Stdin();
        var result = await AccountSnapshotQualification.CollectAsync(stdin, http, stop.Token);
        Assert.Equal("ACCOUNT_SNAPSHOT_REFUSED", result.Status); Assert.Empty(venue.Methods); AssertSafe(result);
    }

    [Fact]
    public async Task FreshnessIsCheckedAgainWhenConstructingDiagnostics()
    {
        var clock = new Clock { ExpireOnAssessment = true }; using var venue = new Venue(); using var http = new HttpClient(venue); using var stdin = Stdin();
        var result = await AccountSnapshotQualification.CollectAsync(stdin, http, CancellationToken.None, clock);
        Assert.Equal("ACCOUNT_SNAPSHOT_INCOMPLETE", result.Status); Assert.True(result.QueriesCompleted);
        Assert.False(result.FreshAtAssessment); Assert.False(result.BalanceObserved); Assert.Equal(0, result.PositionCount);
        Assert.Contains("StaleObservation", result.Problems); AssertSafe(result);
    }

    private static void AssertSafe(AccountSnapshotQualification.Diagnostics result)
    {
        Assert.False(result.AccountWideCoverage); Assert.False(result.VenueCoherent); Assert.False(result.AccountReady);
        Assert.False(result.DurableProjectionPublished); Assert.False(result.TradingReady); Assert.False(result.DatabaseUsed);
        Assert.Equal(0, result.MutatingCalls); Assert.Equal(0, result.DurableWrites);
        var output = JsonSerializer.Serialize(result);
        foreach (var value in new[] { "fixture-private-client", "fixture-private-secret", "never-output-private-error", "654321", "15234.987654321", "98765.432123", "71.125", "CashBalance", "Equity", "NativeSize", "observed-native:", "user_id", "access_token" })
            Assert.DoesNotContain(value, output);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow; internal bool ExpireOnAssessment; private int reads;
        public override DateTimeOffset GetUtcNow() => ExpireOnAssessment && ++reads >= 8 ? Now.AddSeconds(31) : Now;
    }

    private sealed class Venue : HttpMessageHandler
    {
        internal string Mode = "valid"; internal Clock? Clock; internal readonly List<string> Methods = new(); private int summaries;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("www.deribit.com", request.RequestUri!.Host);
            var method = request.RequestUri.AbsolutePath.Replace("/api/v2/", ""); Methods.Add(method);
            object? result; long id = 1;
            if (method == "public/get_instruments")
            {
                Assert.Equal(HttpMethod.Get, request.Method); Assert.Null(request.Headers.Authorization);
                Assert.Equal("?currency=ETH&kind=future&expired=false", request.RequestUri.Query);
                if (Mode == "catalog-http") return Reply(HttpStatusCode.BadGateway, "never-output-private-error");
                if (Mode == "catalog-json") return Reply(HttpStatusCode.OK, "{never-output-private-error");
                result = new[] { new { instrument_name = Mode == "absent" ? "OTHER" : "ETH-PERPETUAL", kind = "future",
                    instrument_type = Mode == "linear" ? "linear" : "reversed", base_currency = Mode == "base" ? "BTC" : "ETH",
                    quote_currency = "USD", settlement_currency = Mode == "base" ? "BTC" : "ETH", is_active = Mode != "inactive" } };
            }
            else
            {
                Assert.Equal(HttpMethod.Post, request.Method); Assert.Empty(request.RequestUri.Query);
                using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var root = document.RootElement; id = root.GetProperty("id").GetInt64();
                Assert.Equal(method, root.GetProperty("method").GetString()); var p = root.GetProperty("params");
                if (method == "public/auth")
                {
                    Assert.Null(request.Headers.Authorization); Assert.Equal("fixture-private-client", p.GetProperty("client_id").GetString());
                    var scope = p.GetProperty("scope").GetString()!; Assert.DoesNotContain("read_write", scope);
                    result = new { access_token = "fixture-private-token", expires_in = 300,
                        scope = Mode == "grant" ? scope.Replace("account:read", "account:read_write") : scope };
                }
                else
                {
                    Assert.Equal("fixture-private-token", request.Headers.Authorization?.Parameter);
                    Assert.Equal("ETH", p.GetProperty("currency").GetString()); Assert.Equal(2, p.EnumerateObject().Count());
                    if (method == "private/get_account_summary")
                    {
                        Assert.True(p.GetProperty("extended").GetBoolean()); summaries++;
                        if (Mode == "stale" && summaries == 2) Clock!.Now = Clock.Now.AddSeconds(31);
                        result = new { id = Mode == "changed-account" && summaries == 2 ? 1 : 654321,
                            type = Mode == "subaccount" ? "subaccount" : "main", currency = "ETH", balance = 15234.987654321m,
                            equity = 98765.432123m, cross_collateral_enabled = false, portfolio_margining_enabled = false };
                    }
                    else
                    {
                        Assert.Equal("private/get_positions", method); Assert.Equal("future", p.GetProperty("kind").GetString());
                        if (Mode == "protocol") return Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new { jsonrpc = "2.0", id,
                            error = new { code = 1, message = "never-output-private-error" } }));
                        if (Mode == "transport") throw new HttpRequestException("never-output-private-error");
                        result = Mode == "empty" ? Array.Empty<object>() : new[] { new { instrument_name = Mode == "unmapped" ? "OTHER" : "ETH-PERPETUAL",
                            kind = "future", user_id = Mode == "user" ? 1 : 654321, direction = "buy", size = 71.125m, size_currency = 0.0355625m } };
                    }
                }
            }
            return Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }));
            HttpResponseMessage Reply(HttpStatusCode code, string content) => new(code) { RequestMessage = request, Content = new StringContent(content) };
        }
    }
}
