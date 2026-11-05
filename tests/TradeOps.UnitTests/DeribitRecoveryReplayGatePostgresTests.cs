using TradeOps.Application.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitRecoveryReplayGatePostgresTests
{
    [Fact]
    public async Task ActualRetainedReaderAndCommittedReplayShareOwnedGateLifecycle()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var inner = new OmsPostgresPrivateEventCapture(db.Source);
        var capture = new OmsPrivateCaptureReplayTests.Capture { Handler = (e, ct) => inner.CaptureAsync(e, ct) };
        await using var r = await DeribitRecoveryReplayGateTests.Rig.Open(capture);
        r.QueryHook = async _ => await r.Coordinator.TryCaptureReceiptAsync(await r.Receipt());
        var reader = new OmsPostgresPrivateRecoveryReader(db.Source);
        var gate = new DeribitRecoveryEvidenceGate(reader, r, r);
        var result = await gate.AssessWithReplayAsync(r.Request(), r.Coordinator, r.Session);
        Assert.True(result.LocalReplayValidated); Assert.Equal(1, result.BufferedObservations);
        Assert.Equal(0, result.Evidence.RetainedFacts); // Snapshot precedes the real commit during query enumeration.
        Assert.DoesNotContain(DeribitRecoveryEvidenceBlocker.BufferedReplayUnavailable, result.Evidence.Blockers);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.CoherentCutUnavailable, result.Evidence.Blockers);
        Assert.False(result.Evidence.AccountReady); Assert.False(result.Evidence.DurableProjectionPublished);
        var durable = await reader.ReadAsync(Account); Assert.True(durable.IsUsable);
        Assert.Equal(1, durable.AcceptedFactCount); Assert.Single(durable.Observations); Assert.Equal(1, capture.Calls);
    }

    [Fact]
    public async Task ActualCommitWithLostAckBlocksIntegratedReplayAndIsNotResent()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var inner = new OmsPostgresPrivateEventCapture(db.Source);
        var capture = new OmsPrivateCaptureReplayTests.Capture { Handler = async (e, ct) => {
            await inner.CaptureAsync(e, ct); throw new IOException("synthetic lost capture ack"); } };
        await using var r = await DeribitRecoveryReplayGateTests.Rig.Open(capture);
        r.QueryHook = async _ => {
            var receipt = await r.Receipt(); await Assert.ThrowsAsync<IOException>(() => r.Coordinator.TryCaptureReceiptAsync(receipt)); };
        var reader = new OmsPostgresPrivateRecoveryReader(db.Source);
        var result = await new DeribitRecoveryEvidenceGate(reader, r, r).AssessWithReplayAsync(r.Request(), r.Coordinator, r.Session);
        Assert.False(result.LocalReplayValidated); Assert.Equal(0, result.BufferedObservations);
        Assert.Contains(OmsPrivateReplayProblem.CaptureFailed, result.ReplayProblems);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.BufferedReplayUnavailable, result.Evidence.Blockers);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.BufferedReplayInvalid, result.Evidence.Blockers);
        var durable = await reader.ReadAsync(Account); Assert.True(durable.IsUsable);
        Assert.Equal(1, durable.AcceptedFactCount); Assert.Single(durable.Observations); Assert.Equal(1, capture.Calls);
    }
}
