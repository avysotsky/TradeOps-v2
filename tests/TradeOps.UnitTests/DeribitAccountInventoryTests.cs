using System.Net;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitAccountInventoryTests
{
    private static readonly AccountKey Scope = new(new("tenant"), new("DERIBIT"), OmsEnvironment.Testnet, new("synthetic-account"));
    private static DeribitAccountInventoryRequest Query() => new(Scope, Guid.NewGuid(), TimeSpan.FromSeconds(30));
    private static DeribitAccountInventoryReader Reader(HttpClient http) => new(new(Scope,
        new() { ClientId = "fixture-id", ClientSecret = "fixture-secret" }, new(123, false,
            [new("ETH-PERPETUAL", "future", "ETH", OmsPrivateAmountUnit.Usd, "USD")])), http);

    [Fact]
    public async Task UnfilteredInventoryKeepsUnknownProductsAndZeroBalanceCurrenciesWithoutInventingUnits()
    {
        using var venue = new Venue(); using var http = new HttpClient(venue); var q = Query();
        var result = await Reader(http).ReadAsync(q);
        Assert.True(result.QueriesCompleted); Assert.True(result.IsFreshAt(DateTimeOffset.UtcNow)); Assert.Equal(5, venue.Calls);
        Assert.Equal(new[] { "ETH", "USDC" }, result.Balances.Select(x => x.Currency)); Assert.Equal(0, result.Balances[1].CashBalance);
        var position = Assert.Single(result.Positions); Assert.Equal("UNMAPPED-COMBO", position.NativeInstrument);
        Assert.Equal("future_combo", position.Kind); Assert.Equal(-2, position.NativeSize);
        Assert.Equal("trailing_stop", Assert.Single(result.OpenOrders).NativeOrderType);
        Assert.Equal(q.Epoch, result.Epoch); Assert.False(result.VenueCoherent); Assert.False(result.AccountReady);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("private@example.invalid", serialized); Assert.DoesNotContain("fixture-secret", serialized);
    }

    [Theory]
    [InlineData("account", DeribitAccountInventoryProblem.AccountMismatch)]
    [InlineData("position-owner", DeribitAccountInventoryProblem.AccountMismatch)]
    [InlineData("duplicate-currency", DeribitAccountInventoryProblem.InvalidObservation)]
    [InlineData("changed-currencies", DeribitAccountInventoryProblem.InvalidObservation)]
    [InlineData("capacity", DeribitAccountInventoryProblem.Capacity)]
    public async Task InvalidScopeOrIncompleteInventoryDiscardsAllPartialPrivateValues(string mode, DeribitAccountInventoryProblem expected)
    {
        using var venue = new Venue { Mode = mode }; using var http = new HttpClient(venue);
        var result = await Reader(http).ReadAsync(Query() with { MaximumPositions = mode == "capacity" ? 1 : 4096 });
        Assert.False(result.QueriesCompleted); Assert.Contains(expected, result.Problems);
        Assert.Empty(result.Balances); Assert.Empty(result.Positions); Assert.Empty(result.OpenOrders);
    }

    [Fact]
    public async Task CancelledOrMisboundRequestDoesNotAuthenticate()
    {
        using var venue = new Venue(); using var http = new HttpClient(venue); var q = Query(); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(http).ReadAsync(q, stop.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => Reader(http).ReadAsync(q with { Epoch = Guid.Empty }));
        Assert.Equal(0, venue.Calls);
    }

    private sealed class Venue : HttpMessageHandler
    {
        internal string Mode = ""; internal int Calls; private int summaries;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Assert.Equal("test.deribit.com", request.RequestUri!.Host); Assert.Equal(HttpMethod.Post, request.Method); Assert.Empty(request.RequestUri.Query);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var root = body.RootElement;
            var method = root.GetProperty("method").GetString(); var p = root.GetProperty("params"); object result;
            if (method == "public/auth")
            { Assert.Null(request.Headers.Authorization); var scope = p.GetProperty("scope").GetString(); Assert.DoesNotContain("read_write", scope); result = new { access_token = "fixture-token", scope, expires_in = 300 }; }
            else
            {
                Assert.Equal("fixture-token", request.Headers.Authorization?.Parameter);
                if (method == "private/get_account_summaries")
                {
                    Assert.True(p.GetProperty("extended").GetBoolean()); Assert.Equal(1, p.EnumerateObject().Count()); summaries++;
                    object Balance(string currency) => new { currency, balance = currency == "ETH" ? -0.1m : 0m, equity = currency == "ETH" ? 0.25m : 0m,
                        cross_collateral_enabled = false, portfolio_margining_enabled = false };
                    var second = Mode == "duplicate-currency" ? "ETH" : Mode == "changed-currencies" && summaries == 2 ? "BTC" : "USDC";
                    result = new { id = Mode == "account" && summaries == 2 ? 456 : 123, type = "main", email = "private@example.invalid",
                        summaries = new[] { Balance("ETH"), Balance(second) } };
                }
                else if (method == "private/get_positions")
                {
                    Assert.Equal(1, p.EnumerateObject().Count()); Assert.Equal("any", p.GetProperty("currency").GetString());
                    var row = new { instrument_name = "UNMAPPED-COMBO", kind = "future_combo", direction = "sell", size = -2, user_id = Mode == "position-owner" ? 456 : 123 };
                    result = Mode == "capacity" ? new[] { row, row } : new[] { row };
                }
                else
                {
                    Assert.Equal("private/get_open_orders", method); Assert.Empty(p.EnumerateObject());
                    result = new[] { new { instrument_name = "UNMAPPED-SPOT", order_id = "external-order", order_state = "untriggered", order_type = "trailing_stop" } };
                }
            }
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt64(), result })) };
        }
    }
}
