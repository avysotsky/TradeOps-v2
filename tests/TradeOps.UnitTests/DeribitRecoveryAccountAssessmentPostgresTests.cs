using System.Net;
using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitRecoveryAccountAssessmentPostgresTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealAccountReaderOverlappingActualCommitRetainsAllRecoveryGates(bool loseAck)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var durable = new OmsPostgresPrivateEventCapture(db.Source);
        var capture = new OmsPrivateCaptureReplayTests.Capture { Handler = async (e, ct) => {
            var result = await durable.CaptureAsync(e, ct);
            if (loseAck) throw new IOException("synthetic lost ack"); return result; } };
        await using var r = await DeribitRecoveryReplayGateTests.Rig.Open(capture);
        using var venue = new Venue { DuringPositions = async ct => {
            var receipt = await r.Receipt();
            try { await r.Coordinator.TryCaptureReceiptAsync(receipt, ct); }
            catch (IOException) when (loseAck) { } // The real commit survives; the replay coordinator keeps its failure.
        } };
        using var http = new HttpClient(venue);
        var account = new DeribitAccountSnapshotReader(new(Account, new() { ClientId = "fixture-id", ClientSecret = "fixture-secret" },
            new(123, false, new[] { new DeribitPrivateInstrument("ETH-PERPETUAL", "future", "ETH", OmsPrivateAmountUnit.Usd, "USD") })), http);
        var retained = new OmsPostgresPrivateRecoveryReader(db.Source); var gate = new DeribitRecoveryEvidenceGate(retained, r, r); var q = r.Request();
        var result = await gate.AssessAccountWithReplayAsync(q, DeribitRecoveryAccountAssessmentTests.Request(q), account, r.Coordinator, r.Session);
        Assert.True(result.ScopedAccountObservationsValidated); Assert.True(result.BalanceObserved); Assert.Equal(1, result.AcceptedPositionRows);
        Assert.Equal(0, result.Evidence.RetainedFacts); Assert.Equal(!loseAck, result.Replay!.LocalReplayValidated);
        Assert.Equal(loseAck ? 0 : 1, result.Replay.BufferedObservations); Assert.Equal(4, venue.Calls); Assert.Equal(1, capture.Calls);
        if (loseAck)
        {
            Assert.Contains(OmsPrivateReplayProblem.CaptureFailed, result.Replay.ReplayProblems);
            Assert.Contains(DeribitRecoveryEvidenceBlocker.BufferedReplayUnavailable, result.Evidence.Blockers);
        }
        else Assert.DoesNotContain(DeribitRecoveryEvidenceBlocker.BufferedReplayUnavailable, result.Evidence.Blockers);
        DeribitRecoveryAccountAssessmentTests.AssertBlocked(result);
        var actual = await retained.ReadAsync(Account); Assert.True(actual.IsUsable);
        Assert.Equal(1, actual.AcceptedFactCount); Assert.Single(actual.Observations);
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("CashBalance", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("NativeSize", JsonSerializer.Serialize(result));
    }

    private sealed class Venue : HttpMessageHandler
    {
        internal int Calls; internal Func<CancellationToken, Task> DuringPositions = _ => Task.CompletedTask;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Assert.Equal("test.deribit.com", request.RequestUri!.Host); Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Empty(request.RequestUri.Query);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement; var method = root.GetProperty("method").GetString(); var p = root.GetProperty("params"); object result;
            if (method == "public/auth")
            {
                Assert.Null(request.Headers.Authorization); var scope = p.GetProperty("scope").GetString()!;
                Assert.DoesNotContain("read_write", scope); result = new { access_token = "fixture-token", scope, expires_in = 300 };
            }
            else
            {
                Assert.Equal("fixture-token", request.Headers.Authorization?.Parameter); Assert.Equal("ETH", p.GetProperty("currency").GetString());
                Assert.Equal(2, p.EnumerateObject().Count());
                if (method == "private/get_account_summary")
                {
                    Assert.True(p.GetProperty("extended").GetBoolean()); result = new { id = 123, type = "main", currency = "ETH",
                        balance = -0.125m, equity = 0.75m, cross_collateral_enabled = false, portfolio_margining_enabled = false };
                }
                else
                {
                    Assert.Equal("private/get_positions", method); Assert.Equal("future", p.GetProperty("kind").GetString());
                    await DuringPositions(ct); result = new[] { new { instrument_name = "ETH-PERPETUAL", kind = "future", user_id = 123,
                        direction = "buy", size = 10, size_currency = 0.005m } };
                }
            }
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(JsonSerializer.Serialize(new {
                jsonrpc = "2.0", id = root.GetProperty("id").GetInt64(), result })) };
        }
    }
}
