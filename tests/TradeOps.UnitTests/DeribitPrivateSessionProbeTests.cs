using System.Net;
using System.Text;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.DeribitPrivateSessionProbe;
using TradeOps.Domain.Oms;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;

public sealed class DeribitPrivateSessionProbeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuietVerifiedSession_RecordsCorrelatedHeartbeat_WithoutDurableCaptureClaim(bool sub)
    {
        var socket = new Socket { AccountType = sub ? "subaccount" : "main", GrantedScope = "connection trade:read account:read name:synthetic-label",
            OnSubscribe = (s, id, p) => { s.Heartbeat(); s.Reply(id, p.GetProperty("channels")); } };
        var input = Input(); input.AccountType = socket.AccountType; input.Validate();
        var result = await PrivateSessionProbe.ObserveAsync(input, Plan(sub: sub), settings => Stream(() => socket, settings),
            TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.Equal("LIVE_SESSION_PASS_NO_DURABLE_CAPTURE", result.Status);
        Assert.Equal(1, result.Diagnostics!.Epochs);
        Assert.Equal(1, result.Diagnostics.Authenticated);
        Assert.Equal(1, result.Diagnostics.AccountsVerified);
        Assert.Equal(1, result.Diagnostics.SubscriptionsConfirmed);
        Assert.Equal(1, result.Diagnostics.HeartbeatChallenges);
        Assert.Equal(1, result.Diagnostics.HeartbeatReplies);
        Assert.Equal(0, result.Diagnostics.CaptureReturns);
        Assert.False(result.IndependentlyVerifiedAccount);
        Assert.False(result.AccountCoverageQualified);
        Assert.False(result.DatabaseUsed);
        Assert.False(result.TradingReady);
        Assert.Equal(0, result.DurableWrites);
        Assert.True(socket.Aborted);
        Assert.DoesNotContain("synthetic-secret", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("observed-native", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("synthetic-label", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task WrongAccount_FailsBeforeSubscribe_AndDoesNotExposeRemoteIdentity()
    {
        var socket = new Socket { AccountId = 987654321 };
        var result = await PrivateSessionProbe.ObserveAsync(Input(), Plan(), settings => Stream(() => socket, settings),
            TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.Equal("LIVE_SESSION_FAIL", result.Status);
        Assert.Equal(1, result.Diagnostics!.Authenticated);
        Assert.Equal(0, result.Diagnostics.AccountsVerified);
        Assert.Equal(0, result.Diagnostics.SubscriptionsConfirmed);
        Assert.Equal(OmsPrivateStreamProblem.AccountMismatch, result.Diagnostics.LastProblem);
        Assert.DoesNotContain("987654321", JsonSerializer.Serialize(result));
        Assert.True(socket.Aborted);
    }

    [Fact]
    public async Task ActualPrivateEvent_StopsProbe_WithoutFakingAnInboxCommit()
    {
        var socket = new Socket { OnSubscribe = (s, id, p) => { s.Emit(Trade()); s.Reply(id, p.GetProperty("channels")); } };
        var result = await PrivateSessionProbe.ObserveAsync(Input(), Plan(), settings => Stream(() => socket, settings),
            TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.Equal("PRIVATE_EVENT_OBSERVED_NOT_STORED", result.Status);
        Assert.Equal(1, result.UnstoredPrivateEvents);
        Assert.Equal(1, result.Diagnostics!.NormalizedEvents);
        Assert.Equal(0, result.Diagnostics.CaptureReturns);
        Assert.Equal(0, result.DurableWrites);
        Assert.DoesNotContain("trade-1", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("order-1", JsonSerializer.Serialize(result));
        Assert.True(socket.Aborted);
    }

    [Theory]
    [InlineData("purpose")]
    [InlineData("invalid-type")]
    [InlineData("pin")]
    [InlineData("duration")]
    public void Input_RequiresExplicitDiagnosticPurposeBoundedTimeAndSupportedIdentity(string scenario)
    {
        var input = Input();
        if (scenario == "purpose") input.Purpose = "TRADE";
        if (scenario == "invalid-type") input.AccountType = "unknown";
        if (scenario == "pin") input.ExpectedAccountId = 0;
        if (scenario == "duration") input.ObserveSeconds = 9999;
        Assert.Throws<InvalidOperationException>(input.Validate);
        Assert.DoesNotContain("synthetic-secret", input.ToString());
    }

    [Fact]
    public async Task Manifest_RejectsUnqualifiedMixedSettlement_InsteadOfSilentlyDroppingIt()
    {
        using var http = new HttpClient(new CatalogHandler());
        var catalog = new DeribitInstrumentCatalog(http);
        var futures = await catalog.ReadAsync(OmsEnvironment.Mainnet, "BTC", "future");
        var options = await catalog.ReadAsync(OmsEnvironment.Mainnet, "BTC", "option");
        Assert.Throws<InvalidOperationException>(() => PrivateSessionProbe.CreateManifest(futures, options));
    }

    private static PrivateSessionProbeInput Input() => new() { Purpose = "READ_ONLY_WS_QUALIFICATION",
        ClientId = "synthetic-client", ClientSecret = "synthetic-secret", ExpectedAccountId = 123, AccountType = "main" };
    private sealed class CatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var future = request.RequestUri!.Query.Contains("kind=future", StringComparison.Ordinal);
            var item = new { instrument_name = future ? "BTC-PERPETUAL" : "BTC_USDC-OPTION", kind = future ? "future" : "option",
                instrument_type = future ? "reversed" : "linear", base_currency = "BTC", quote_currency = future ? "USD" : "USDC",
                settlement_currency = future ? "BTC" : "USDC", is_active = true };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", result = new[] { item } }), Encoding.UTF8, "application/json") });
        }
    }
}
