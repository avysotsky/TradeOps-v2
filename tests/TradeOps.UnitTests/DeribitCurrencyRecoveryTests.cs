using System.Net;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitCurrencyRecoveryTests
{
    private static readonly AccountKey Scope = new(new("tenant"), new("DERIBIT"), OmsEnvironment.Testnet, new("synthetic-account"));
    private static readonly DateTimeOffset From = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds());
    private static DeribitCurrencyRecoveryQuery Query() => new(Scope, Guid.NewGuid(), "ETH", "future", From, From.AddMinutes(1), TimeSpan.FromSeconds(1), 2);
    private static DeribitCurrencyRecoveryQueries Reader(HttpClient http) => new(new(Scope,
        new() { ClientId = "fixture-id", ClientSecret = "fixture-secret" }, new(123, false, [
            new("ETH-PERPETUAL", "future", "ETH", OmsPrivateAmountUnit.Usd, "USD"),
            new("ETH-25DEC26", "future", "ETH", OmsPrivateAmountUnit.Usd, "USD") ])), http);

    [Fact]
    public async Task GroupTraversalFindsOtherInstrumentAndPreservesSameMillisecondFillsAcrossOpaqueCursor()
    {
        using var venue = new Venue(); using var http = new HttpClient(venue);
        var result = await Reader(http).CollectAsync(Query());
        Assert.True(result.TraversalComplete); Assert.Equal(5, result.Sources.Count);
        Assert.Equal(3, result.Observations.Count); Assert.Equal(2, result.Observations.Select(x => x.Facts.NativeInstrument).Distinct().Count());
        Assert.Equal(2, result.Sources.Single(x => x.Source == "trades-recent").Pages);
        Assert.Equal(3, result.Observations.Select(x => x.Facts.ExchangeTradeId).Distinct().Count());
        Assert.False(result.HistoricalUnfilledOrdersCovered); Assert.False(result.OrderHistoryComplete);
        Assert.False(result.VenueCoherent); Assert.False(result.AccountWideCoverage);
        Assert.All(result.Observations, x => { Assert.Equal(Scope, x.Scope); Assert.Equal(result.Epoch, x.Observation.StreamEpoch); });
    }

    [Theory]
    [InlineData("unknown", DeribitCurrencyRecoveryProblem.UnmappedInstrument)]
    [InlineData("changed-account", DeribitCurrencyRecoveryProblem.BindingInvalid)]
    [InlineData("collision", DeribitCurrencyRecoveryProblem.FactCollision)]
    [InlineData("repeat", DeribitCurrencyRecoveryProblem.Pagination)]
    [InlineData("backwards", DeribitCurrencyRecoveryProblem.Pagination)]
    public async Task UnknownActivityOrUnprovenTraversalRefusesWholeBatch(string mode, DeribitCurrencyRecoveryProblem expected)
    {
        using var venue = new Venue { Mode = mode }; using var http = new HttpClient(venue);
        var result = await Reader(http).CollectAsync(Query());
        Assert.False(result.TraversalComplete); Assert.Contains(expected, result.Problems); Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task CallerCancellationStopsWithoutAnotherRequest()
    {
        using var stop = new CancellationTokenSource(); using var venue = new Venue { Stop = stop }; using var http = new HttpClient(venue);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(http).CollectAsync(Query(), stop.Token));
        Assert.Equal(3, venue.Calls);
    }

    [Fact]
    public async Task InvalidGroupOrPageBoundCannotAuthenticate()
    {
        using var venue = new Venue(); using var http = new HttpClient(venue); var q = Query();
        await Assert.ThrowsAsync<ArgumentException>(() => Reader(http).CollectAsync(q with { Currency = "BTC" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Reader(http).CollectAsync(q with { PageSize = 1 }));
        Assert.Equal(0, venue.Calls);
    }

    [Theory]
    [InlineData("ETH")] [InlineData("USDC")] [InlineData("USDT")]
    public async Task AllKindTraversalCanProveEmptyHistoryWithoutPretendingUnknownInstrumentsHaveMetadata(string currency)
    {
        using var venue = new Venue { Mode = "empty", Currency = currency, Kind = "any" }; using var http = new HttpClient(venue);
        var result = await Reader(http).CollectAsync(Query() with { Currency = currency, Kind = "any" });
        Assert.True(result.TraversalComplete); Assert.Empty(result.Observations); Assert.Equal(currency, result.Currency);
        Assert.Equal("any", result.Kind); Assert.False(result.AccountWideCoverage);
    }

    private sealed class Venue : HttpMessageHandler
    {
        internal string Mode = "", Currency = "ETH", Kind = "future"; internal CancellationTokenSource? Stop; internal int Calls;
        private int summaries;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Assert.Equal("test.deribit.com", request.RequestUri!.Host); Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Empty(request.RequestUri.Query);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var root = body.RootElement;
            var method = root.GetProperty("method").GetString()!; var p = root.GetProperty("params"); object result;
            Assert.Equal("/api/v2/" + method, request.RequestUri.AbsolutePath);
            if (method == "public/auth")
            {
                Assert.Null(request.Headers.Authorization); var scope = p.GetProperty("scope").GetString()!;
                Assert.Contains("trade:read ", scope); Assert.DoesNotContain("read_write", scope);
                result = new { access_token = "fixture-token", scope, expires_in = 300 };
            }
            else
            {
                Assert.Equal("fixture-token", request.Headers.Authorization?.Parameter); Assert.Equal(Currency, p.GetProperty("currency").GetString());
                if (method == "private/get_account_summary") result = new { id = ++summaries == 2 && Mode == "changed-account" ? 999 : 123, type = "main", currency = Currency };
                else
                {
                    Assert.Equal(Kind, p.GetProperty("kind").GetString());
                    Assert.False(p.TryGetProperty("instrument_name", out _)); Assert.False(p.TryGetProperty("start_seq", out _));
                    if (method == "private/get_open_orders_by_currency")
                    {
                        Stop?.Cancel(); ct.ThrowIfCancellationRequested();
                        result = Mode == "unknown" ? new[] { new { instrument_name = "UNMAPPED-NATIVE" } } : (object)Array.Empty<object>();
                    }
                    else if (method == "private/get_order_history_by_currency")
                    {
                        Assert.Equal(!p.GetProperty("historical").GetBoolean(), p.GetProperty("include_unfilled").GetBoolean());
                        result = Array.Empty<object>();
                    }
                    else
                    {
                        Assert.Equal("private/get_user_trades_by_currency", method); Assert.Equal("asc", p.GetProperty("sorting").GetString());
                        var historical = p.GetProperty("historical").GetBoolean();
                        var cursor = p.TryGetProperty("start_id", out var raw) ? raw.GetString() : null;
                        Assert.Equal(cursor is null, p.TryGetProperty("start_timestamp", out _));
                        if (cursor is not null) Assert.Equal("ETH-opaque_B", cursor);
                        var a = Trade("ETH-opaque_A", "ETH-PERPETUAL", 100);
                        var b = Trade("ETH-opaque_B", "ETH-25DEC26", 1); // trade_seq is not global across instruments
                        var c = Trade("ETH-opaque_C", "ETH-PERPETUAL", 101);
                        if (Mode == "empty") result = new { trades = Array.Empty<object>(), has_more = false };
                        else if (historical) result = new { trades = new[] { Mode == "collision" ? Trade("ETH-opaque_A", "ETH-PERPETUAL", 100, price: 2001) : a, b }, has_more = false };
                        else if (cursor is null) result = new { trades = new[] { a, b }, has_more = true };
                        else if (Mode == "repeat") result = new { trades = new[] { a, b }, has_more = true };
                        else result = new { trades = new[] { b, Mode == "backwards" ? Trade("ETH-opaque_C", "ETH-PERPETUAL", 101, before: true) : c }, has_more = false };
                    }
                }
            }
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(new {
                jsonrpc = "2.0", id = root.GetProperty("id").GetInt64(), result })) };
        }
        private static object Trade(string id, string instrument, int seq, decimal price = 2000, bool before = false) => new {
            instrument_name = instrument, order_id = "order-" + instrument, trade_id = id, trade_seq = seq,
            direction = "buy", amount = 1, price, fee = 0.0001m, fee_currency = "ETH", label = "",
            timestamp = From.AddSeconds(before ? 1 : 2).ToUnixTimeMilliseconds() };
    }
}
