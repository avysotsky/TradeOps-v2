using System.Net;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitRecoveryQueryTests
{
    private static readonly DateTimeOffset End = DateTimeOffset.UtcNow.AddMinutes(-1);
    private static readonly DateTimeOffset Start = End.AddHours(-1);
    private static readonly AccountKey Scope = new(new("tenant"), new("DERIBIT"), OmsEnvironment.Mainnet, new("account"));
    private static DeribitRecoveryQuery Query() => new(Scope, Guid.NewGuid(), "ETH-PERPETUAL", Start, End, TimeSpan.FromSeconds(10), PageSize: 2);
    private static DeribitRecoveryQueries Collector(HttpClient http) => new(new(Scope,
        new() { WebSocketUrl = "wss://www.deribit.com/ws/api/v2", ClientId = "fixture-id", ClientSecret = "fixture-secret" },
        new(123, false, new[] { new DeribitPrivateInstrument("ETH-PERPETUAL", "future", "ETH", OmsPrivateAmountUnit.Usd, "USD") })), http);

    [Fact]
    public async Task EmptyTraversalIsExplicitlyNotCoherentOrAccountWide()
    {
        using var venue = new Venue(); using var http = new HttpClient(venue);
        var query = Query(); var result = await Collector(http).CollectAsync(query);
        Assert.True(result.TraversalComplete); Assert.False(result.CoherentRecovery);
        Assert.Equal(5, result.Sources.Count); Assert.Equal(8, venue.Calls);
        Assert.Empty(result.Observations); Assert.Equal(Scope, result.Scope); Assert.Equal(query.Epoch, result.Epoch);
        Assert.Equal((query.FromUtc-query.Overlap).ToUnixTimeMilliseconds(), result.QueryFromUtc.ToUnixTimeMilliseconds());
        Assert.All(result.Sources, x => Assert.True(x.Exhausted));
    }

    [Fact]
    public async Task PaginatedBothStoresPreserveSameMillisecondFillsAndDeduplicateOverlap()
    {
        using var venue = new Venue { Mode = "pages" }; using var http = new HttpClient(venue);
        var result = await Collector(http).CollectAsync(Query());
        Assert.True(result.TraversalComplete);
        Assert.Equal(5, result.Observations.Count); // two order revisions, three executions
        Assert.Equal(3, result.Observations.Count(x => x.Facts.Kind == OmsJournalEventKind.Execution));
        Assert.All(result.Observations, x => {
            Assert.Equal(Scope, x.Scope); Assert.Equal(result.Epoch, x.Observation.StreamEpoch);
            Assert.Equal(OmsPrivateCaptureSource.RecoveryQuery, x.Observation.Source);
            Assert.Equal(OmsPrivateAmountUnit.Usd, x.Facts.AmountUnit); Assert.Equal("USD", x.Facts.AmountCurrency);
        });
        Assert.Equal(2, result.Sources.Single(x => x.Source == "orders-historical").Pages);
        Assert.Equal(2, result.Sources.Single(x => x.Source == "trades-historical").Pages);
    }

    [Theory]
    [InlineData("wrong-account", DeribitRecoveryQueryProblem.AccountMismatch)]
    [InlineData("changed-account", DeribitRecoveryQueryProblem.AccountMismatch)]
    [InlineData("grant", DeribitRecoveryQueryProblem.Authentication)]
    [InlineData("wrong-id", DeribitRecoveryQueryProblem.Protocol)]
    [InlineData("rpc-error", DeribitRecoveryQueryProblem.Protocol)]
    [InlineData("http-error", DeribitRecoveryQueryProblem.Transport)]
    [InlineData("oversized", DeribitRecoveryQueryProblem.Capacity)]
    [InlineData("instrument", DeribitRecoveryQueryProblem.Protocol)]
    [InlineData("missing-fee", DeribitRecoveryQueryProblem.Protocol)]
    [InlineData("repeated-page", DeribitRecoveryQueryProblem.Pagination)]
    [InlineData("empty-more", DeribitRecoveryQueryProblem.Pagination)]
    [InlineData("bad-sequence", DeribitRecoveryQueryProblem.Pagination)]
    [InlineData("collision", DeribitRecoveryQueryProblem.FactCollision)]
    [InlineData("bound", DeribitRecoveryQueryProblem.Capacity)]
    [InlineData("deadline", DeribitRecoveryQueryProblem.Deadline)]
    [InlineData("fact-limit", DeribitRecoveryQueryProblem.Capacity)]
    public async Task IncompleteOrUntrustedDataClearsBatch(string mode, DeribitRecoveryQueryProblem problem)
    {
        using var venue = new Venue { Mode = mode }; using var http = new HttpClient(venue);
        var result = await Collector(http).CollectAsync(Query() with { MaximumPagesPerSource = mode == "bound" ? 1 : 10,
            MaximumFacts = mode == "fact-limit" ? 1 : 5000 });
        Assert.False(result.TraversalComplete); Assert.Contains(problem, result.Problems); Assert.Empty(result.Observations);
        if (mode == "http-error") Assert.Equal(429, result.HttpStatusCode);
    }

    [Theory]
    [InlineData("scope")] [InlineData("instrument")] [InlineData("future")] [InlineData("window")]
    [InlineData("overlap")] [InlineData("size")] [InlineData("pages")] [InlineData("facts")]
    public async Task InvalidQueryRejectedBeforeNetwork(string mutation)
    {
        using var venue = new Venue(); using var http = new HttpClient(venue);
        var q = Query(); q = mutation switch {
            "scope" => q with { Scope = new(new("other"), Scope.Venue, Scope.Environment, Scope.Account) },
            "instrument" => q with { NativeInstrument = "UNKNOWN" },
            "future" => q with { ThroughUtc = DateTimeOffset.UtcNow.AddDays(1) },
            "window" => q with { FromUtc = q.ThroughUtc.AddDays(-8) },
            "overlap" => q with { Overlap = TimeSpan.Zero }, "size" => q with { PageSize = 1001 },
            "pages" => q with { MaximumPagesPerSource = 21 }, _ => q with { MaximumFacts = 10001 } };
        await Assert.ThrowsAsync<ArgumentException>(() => Collector(http).CollectAsync(q)); Assert.Equal(0, venue.Calls);
    }

    [Fact]
    public async Task CancellationPropagatesAndNeverRetries()
    {
        using var venue = new Venue(); using var http = new HttpClient(venue);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collector(http).CollectAsync(Query(), stop.Token));
        Assert.Equal(0, venue.Calls);
    }

    private sealed class Venue : HttpMessageHandler
    {
        internal string Mode = "empty";
        internal int Calls;
        private int identities;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (Mode == "deadline") throw new OperationCanceledException();
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("www.deribit.com", request.RequestUri!.Host);
            Assert.Equal("", request.RequestUri.Query); Assert.False(request.Headers.Contains("Cookie"));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = json.RootElement; var method = root.GetProperty("method").GetString()!; var p = root.GetProperty("params");
            object result;
            if (method == "public/auth")
            {
                Assert.Null(request.Headers.Authorization);
                var scope = p.GetProperty("scope").GetString()!;
                Assert.Contains("trade:read ", scope); Assert.DoesNotContain("read_write", scope);
                result = new { access_token = "fixture-token", expires_in = 300, scope = Mode == "grant" ? scope.Replace("trade:read", "trade:read_write") : scope };
            }
            else
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme); Assert.Equal("fixture-token", request.Headers.Authorization?.Parameter);
                if (method == "private/get_account_summary")
                    result = new { id = Mode == "wrong-account" || Mode == "changed-account" && ++identities == 2 ? 124 : 123, type = "main", currency = "ETH" };
                else
                {
                    Assert.Equal("ETH-PERPETUAL", p.GetProperty("instrument_name").GetString());
                    var pages = Mode is "pages" or "repeated-page" or "bound";
                    if (method == "private/get_open_orders_by_instrument")
                        result = Mode == "instrument" ? new[] { Order("open", "OTHER") }
                            : Mode == "fact-limit" ? new[] { Order("open"), Order("open") } : Array.Empty<object>();
                    else if (method == "private/get_order_history_by_instrument")
                    {
                        Assert.True(p.GetProperty("include_old").GetBoolean()); Assert.True(p.GetProperty("include_unfilled").GetBoolean());
                        var offset = p.GetProperty("offset").GetInt32();
                        result = pages && (offset == 0 || Mode == "repeated-page")
                            ? new[] { Order("open"), Order("cancelled") } : Array.Empty<object>();
                    }
                    else
                    {
                        Assert.Equal("private/get_user_trades_by_instrument", method);
                        Assert.Equal("asc", p.GetProperty("sorting").GetString());
                        Assert.Equal(End.ToUnixTimeMilliseconds(), p.GetProperty("end_timestamp").GetInt64());
                        var next = p.TryGetProperty("start_seq", out var s);
                        if (next) { Assert.False(p.TryGetProperty("start_timestamp", out _)); Assert.Equal(3, s.GetInt64()); }
                        var trades = pages ? next ? new[] { Trade(3) } : new[] { Trade(1), Trade(2) }
                            : Mode is "missing-fee" or "bad-sequence" or "collision" ? new[] { Trade(1) } : Array.Empty<Dictionary<string, object>>();
                        if (Mode == "missing-fee") trades[0].Remove("fee_currency");
                        if (Mode == "bad-sequence") trades = new[] { Trade(2), Trade(1) };
                        if (Mode == "collision" && p.GetProperty("historical").GetBoolean()) trades[0]["price"] = 2001;
                        result = new { trades, has_more = Mode == "empty-more" || pages && !next };
                    }
                }
            }
            if (Mode == "http-error") return new(HttpStatusCode.TooManyRequests) { RequestMessage = request };
            if (Mode == "oversized") return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(new string('x', 524289)) };
            var body = Mode == "rpc-error" ? JsonSerializer.Serialize(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt64(), error = new { secret = "do-not-emit" } })
                : JsonSerializer.Serialize(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt64() + (Mode == "wrong-id" ? 1 : 0), result });
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body) };
        }
    }

    private static object Order(string state, string instrument = "ETH-PERPETUAL") => new {
        instrument_name = instrument, order_id = "order", direction = "buy", amount = 3, price = 2000,
        order_state = state, filled_amount = state == "open" ? 0 : 3,
        last_update_timestamp = Start.AddMinutes(state == "open" ? 1 : 2).ToUnixTimeMilliseconds() };
    private static Dictionary<string, object> Trade(long seq) => new() {
        ["instrument_name"] = "ETH-PERPETUAL", ["order_id"] = "order", ["trade_id"] = "trade"+seq,
        ["trade_seq"] = seq, ["direction"] = "buy", ["amount"] = 1, ["price"] = 2000,
        ["timestamp"] = Start.AddMinutes(2).ToUnixTimeMilliseconds(), ["fee"] = 0.001, ["fee_currency"] = "ETH" };
}
