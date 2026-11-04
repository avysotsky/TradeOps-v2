using System.Net;
using System.Text;
using System.Text.Json;
using TradeOps.DeribitTestnetOrderQualification;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;

public sealed class DeribitTestnetOrderQualificationTests
{
    [Theory]
    [InlineData("Mainnet", true)]
    [InlineData("Testnet", false)]
    public async Task MainnetOrMissingApproval_RefusedBeforeAnyRequest(string environment, bool approval)
    {
        var input = Input(); input.Environment = environment; input.ApprovedOneOrderAndCancellation = approval;
        var transport = new Venue(); using var http = new HttpClient(transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => TestnetOrderQualification.RunAsync(input, http,
            Path.GetTempPath(), CancellationToken.None));
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("connection trade:read_write account:read wallet:read_write")]
    [InlineData("connection trade:read_write account:read account:read")]
    [InlineData("connection trade:read account:read")]
    [InlineData("connection trade:read_write account:read mainaccount subaccount")]
    public void ExpandedOrMalformedGrant_Refused(string scope) =>
        Assert.Throws<InvalidOperationException>(() => TestnetOrderQualification.ValidateGrant(scope));

    [Theory]
    [InlineData(20, 0)]
    [InlineData(10, -10)]
    public void ExcessiveAmountOrStaleBook_Refused(decimal amount, int ageSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<InvalidOperationException>(() => TestnetOrderQualification.ValidateRisk(
            Element(Metadata(amount)), Element(Book(now.AddSeconds(ageSeconds))), now));
    }

    [Fact]
    public void PassiveMinimumOrder_IsExplicitlyUsdBounded()
    {
        var now = DateTimeOffset.UtcNow;
        var terms = TestnetOrderQualification.ValidateRisk(Element(Metadata()), Element(Book(now)), now);
        Assert.Equal(10, terms.Amount); Assert.Equal(64675, terms.Price);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("lost-place-ack")]
    [InlineData("lost-cancel-ack")]
    [InlineData("unresolved")]
    [InlineData("fill")]
    public async Task RealJournalAndCapture_PlaceOnceThenCancel_AndLostAckNeverResends(string scenario)
    {
        var socket = new Socket();
        var venue = new Venue { Socket = socket, LosePlacementAck = scenario == "lost-place-ack",
            LoseCancellationAck = scenario == "lost-cancel-ack", Unresolved = scenario == "unresolved", HasFill = scenario == "fill" };
        using var http = new HttpClient(venue);
        var directory = Path.Combine(Path.GetTempPath(), "tradeops-testnet-case-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await TestnetOrderQualification.RunAsync(Input(), http, directory, CancellationToken.None,
                settings => Stream(() => socket, settings));
            Assert.Equal(1, venue.Placements); Assert.Equal(1, venue.Cancellations);
            Assert.Equal("test.deribit.com", socket.Endpoint!.Host);
            Assert.Equal(1, result.PlacementAttempts); Assert.Equal(1, result.CancellationAttempts);
            Assert.True(result.DatabaseUsed); Assert.Equal(scenario != "unresolved", result.ExchangeOrderResolved);
            Assert.Equal(scenario is not ("fill" or "unresolved"), result.CancelEventCommitted); Assert.False(result.TradingReady);
            Assert.Equal(scenario == "fill", result.UnexpectedFill); Assert.Equal(0L, result.IncidentRows);
            Assert.Equal(scenario == "unresolved" ? "TESTNET_ORDER_UNRESOLVED" : scenario is "lost-place-ack" or "fill"
                ? "TESTNET_ORDER_CAPTURE_FAILED" : "TESTNET_ORDER_CAPTURE_PASS", result.Status);
            Assert.Equal(scenario != "lost-place-ack", result.OpenEventCommitted);
            Assert.DoesNotContain("synthetic-secret", JsonSerializer.Serialize(result));
            Assert.DoesNotContain("order-acceptance", JsonSerializer.Serialize(result));
            var calls = venue.Calls;
            await Assert.ThrowsAsync<InvalidOperationException>(() => TestnetOrderQualification.RunAsync(Input(), http,
                directory, CancellationToken.None, settings => Stream(() => socket, settings)));
            Assert.Equal(calls, venue.Calls); // Existing marker blocks a second order, even after PASS.
        }
        finally
        {
            // Exact GUID-owned temporary test directory; contains only synthetic markers.
            RemoveOwnMarkerDirectory(directory);
        }
    }

    [Fact]
    public async Task WrongNativeAccount_RefusedBeforePlacementOrSubscription()
    {
        var input = Input(); input.ExpectedAccountId = 999;
        var venue = new Venue(); using var http = new HttpClient(venue);
        var directory = Path.Combine(Path.GetTempPath(), "tradeops-testnet-case-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => TestnetOrderQualification.RunAsync(input, http,
                directory, CancellationToken.None));
            Assert.Equal(0, venue.Placements); Assert.Equal(0, venue.Cancellations);
        }
        finally { RemoveOwnMarkerDirectory(directory); }
    }

    private static TestnetOrderInput Input() => new() { Purpose = "TESTNET_ONE_POSTONLY_ORDER_CANCEL",
        Environment = "Testnet", ApprovedOneOrderAndCancellation = true, DedicatedTestAccount = true,
        ClientId = "synthetic-client", ClientSecret = "synthetic-secret", ExpectedAccountId = 123, AccountType = "main" };
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);
    private static void RemoveOwnMarkerDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        File.Delete(Path.Combine(directory, "attempt.json"));
        File.Delete(Path.Combine(directory, "run.lock"));
        Directory.Delete(directory); // No recursive deletion.
    }
    private static object Metadata(decimal amount = 10) => new { instrument_name = "BTC-PERPETUAL", kind = "future",
        instrument_type = "reversed", base_currency = "BTC", quote_currency = "USD", settlement_currency = "BTC",
        is_active = true, min_trade_amount = amount, tick_size = 0.5m, tick_size_steps = Array.Empty<object>() };
    private static object Book(DateTimeOffset now) => new { instrument_name = "BTC-PERPETUAL", state = "open",
        best_bid_price = 65000m, best_ask_price = 65001m, min_price = 60000m, max_price = 70000m,
        timestamp = now.ToUnixTimeMilliseconds() };

    private sealed class Venue : HttpMessageHandler
    {
        internal Socket? Socket;
        internal int Calls, Placements, Cancellations;
        internal bool LosePlacementAck, LoseCancellationAck, Unresolved, HasFill;
        private string label = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https", request.RequestUri!.Scheme); Assert.Equal("test.deribit.com", request.RequestUri.Host);
            Calls++;
            using var message = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = message.RootElement; var method = root.GetProperty("method").GetString();
            var parameters = root.GetProperty("params"); object result;
            switch (method)
            {
                case "public/auth": result = new { access_token = "synthetic-token", scope = "connection trade:read_write account:read" }; break;
                case "private/get_account_summary": result = new { id = 123, type = "main" }; break;
                case "private/get_open_orders_by_currency": case "private/get_positions": result = Array.Empty<object>(); break;
                case "public/get_instrument": result = Metadata(); break;
                case "public/get_order_book": result = Book(DateTimeOffset.UtcNow); break;
                case "private/buy":
                    Placements++; Assert.Equal(1, Placements);
                    Assert.Equal(10, parameters.GetProperty("amount").GetDecimal());
                    Assert.True(parameters.GetProperty("post_only").GetBoolean());
                    Assert.True(parameters.GetProperty("reject_post_only").GetBoolean());
                    label = parameters.GetProperty("label").GetString()!;
                    Socket!.Emit(Notification("open"));
                    if (LosePlacementAck) throw new HttpRequestException("Synthetic lost ACK.");
                    result = new { order = Order("open") }; break;
                case "private/cancel_by_label":
                    Cancellations++; Assert.Equal(label, parameters.GetProperty("label").GetString());
                    Assert.Equal("BTC", parameters.GetProperty("currency").GetString());
                    if (Unresolved) throw new HttpRequestException("Synthetic unresolved cancel.");
                    Socket!.Emit(Notification("cancelled"));
                    if (LoseCancellationAck) throw new HttpRequestException("Synthetic lost cancel ACK.");
                    result = 1; break;
                case "private/get_order_state_by_label":
                    if (Unresolved) throw new HttpRequestException("Synthetic state unavailable.");
                    result = new[] { Order("cancelled") }; break;
                default: throw new InvalidOperationException("Unexpected method.");
            }
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(
                new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt64(), result }), Encoding.UTF8, "application/json") };
        }
        private object Order(string state) => new { instrument_name = "BTC-PERPETUAL", order_id = "order-acceptance",
            label, direction = "buy", amount = 10m, filled_amount = HasFill && state == "cancelled" ? 1m : 0m, price = 64675m, order_state = state,
            last_update_timestamp = 1793793600000L + (state == "cancelled" ? 1 : 0) };
        private string Notification(string state) => JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "subscription",
            @params = new { channel = "user.orders.future.BTC.raw", data = Order(state) } });
    }
}
