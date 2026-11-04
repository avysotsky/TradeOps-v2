using System.Net;
using System.Text;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.DeribitPrivateSessionProbe;
using Xunit;
using InboxRunner = TradeOps.DeribitLiveInboxQualification.LiveInboxQualification;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;

public sealed class DeribitLiveInboxQualificationTests
{
    [Fact]
    public void LinkedFixture_UsesExactlyTheSameSchemaFingerprint()
    {
        Assert.Equal(Support.OmsPostgresTestDatabase.SchemaFingerprint,
            QualificationSupport.OmsPostgresTestDatabase.SchemaFingerprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealStore_CommitsAndDeduplicatesScriptedEvents_WithRestrictedRoleAndNoLegacyWrites(bool sub)
    {
        var socket = new Socket { AccountType = sub ? "subaccount" : "main", OnSubscribe = (s, id, p) =>
        { s.Heartbeat(); s.Emit(Trade()); s.Emit(Trade()); s.Reply(id, p.GetProperty("channels")); } };
        using var http = new HttpClient(new CatalogHandler());
        var input = Input(); input.AccountType = socket.AccountType;
        var result = await InboxRunner.RunWithTransportAsync(input, http, settings => Stream(() => socket, settings),
            TimeSpan.FromSeconds(3), CancellationToken.None);
        Assert.Equal("LIVE_INBOX_CAPTURE_PASS", result.Status);
        Assert.True(result.DatabaseUsed);
        Assert.Equal(1, result.InboxRows);
        Assert.Equal(2, result.ObservationRows);
        Assert.Equal(0, result.IncidentRows);
        Assert.Equal(2, result.Diagnostics!.CaptureReturns);
        Assert.True(result.PrivateEventCaptureQualified);
        Assert.True(result.LegacyUntouched);
        Assert.False(result.IndependentlyVerifiedAccount);
        Assert.False(result.ProductionHierarchyQualified);
        Assert.False(result.TradingReady);
        Assert.True(socket.Aborted);
        Assert.DoesNotContain("synthetic-secret", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("trade-1", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task QuietSession_ProvesConnectionButNeverClaimsAnEventCommit()
    {
        var socket = new Socket { OnSubscribe = (s, id, p) => { s.Heartbeat(); s.Reply(id, p.GetProperty("channels")); } };
        using var http = new HttpClient(new CatalogHandler());
        var result = await InboxRunner.RunWithTransportAsync(Input(), http, settings => Stream(() => socket, settings),
            TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.Equal("LIVE_INBOX_CONNECTED_NO_EVENTS", result.Status);
        Assert.True(result.DatabaseUsed);
        Assert.Equal(0, result.InboxRows);
        Assert.Equal(0, result.ObservationRows);
        Assert.False(result.PrivateEventCaptureQualified);
        Assert.True(result.LegacyUntouched);
    }

    [Fact]
    public async Task WrongAccount_StopsBeforeSubscription_AndLeavesTheCloneEmpty()
    {
        var socket = new Socket { AccountId = 987654321 };
        using var http = new HttpClient(new CatalogHandler());
        var result = await InboxRunner.RunWithTransportAsync(Input(), http, settings => Stream(() => socket, settings),
            TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.Equal("LIVE_INBOX_FAILED", result.Status);
        Assert.Equal(0, result.ObservationRows);
        Assert.Equal(0, result.Diagnostics!.SubscriptionsConfirmed);
        Assert.Equal(OmsPrivateStreamProblem.AccountMismatch, result.Diagnostics.LastProblem);
        Assert.DoesNotContain("987654321", JsonSerializer.Serialize(result));
        Assert.True(socket.Aborted);
    }

    private static PrivateSessionProbeInput Input() => new() { Purpose = InboxRunner.Purpose,
        ClientId = "synthetic-client", ClientSecret = "synthetic-secret", ExpectedAccountId = 123, AccountType = "main" };

    private sealed class CatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var future = request.RequestUri!.Query.Contains("kind=future", StringComparison.Ordinal);
            var item = new { instrument_name = future ? "BTC-PERPETUAL" : "BTC-OPTION", kind = future ? "future" : "option",
                instrument_type = "reversed", base_currency = "BTC", quote_currency = future ? "USD" : "BTC",
                settlement_currency = "BTC", is_active = true };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", result = new[] { item } }), Encoding.UTF8, "application/json") });
        }
    }
}
