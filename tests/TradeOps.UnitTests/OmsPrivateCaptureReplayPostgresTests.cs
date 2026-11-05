using TradeOps.Application.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class OmsPrivateCaptureReplayPostgresTests
{
    [Fact]
    public async Task CommittedReceiptPrefixMatchesIndependentReaderIncludingDuplicates()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var inner = new OmsPostgresPrivateEventCapture(db.Source);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new OmsPrivateCaptureReplayTests.Capture { Handler = async (e, ct) => {
            entered.TrySetResult(); await release.Task.WaitAsync(ct); return await inner.CaptureAsync(e, ct); } };
        await using var c = new OmsPrivateCaptureCoordinator(capture, new());
        var s = await c.SwitchAsync(Account); using var runner = s.AcquireRunner(); var epoch = await c.BeginEpochAsync(s);
        await c.UpdateAsync(s, epoch, OmsPrivateStreamPhase.Recovering, OmsPrivateStreamProblem.AwaitingReconciliation);
        var w = await c.BeginReplayAsync(s, epoch);
        var a = await c.RegisterReceiptAsync(s, epoch, OmsPrivateCaptureReplayTests.Event(epoch));
        var committing = c.TryCaptureReceiptAsync(a);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        OmsPrivateCaptureReceipt b; Task<OmsPrivateReplayCut> sealing;
        try
        {
            b = await c.RegisterReceiptAsync(s, epoch, OmsPrivateCaptureReplayTests.Event(epoch)).WaitAsync(TimeSpan.FromSeconds(2));
            sealing = c.SealReplayAsync(w); Assert.False(sealing.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await committing; var cut = await sealing;
        Assert.Contains(OmsPrivateReplayProblem.PendingCommits, (await c.ReadReplayAsync(cut)).Problems);
        Assert.Equal(OmsPrivateCaptureResult.Duplicate, await c.TryCaptureReceiptAsync(b));
        var replay = await c.ReadReplayAsync(cut); Assert.True(replay.CanReplay);
        Assert.Equal(new[] { OmsPrivateCaptureResult.Inserted, OmsPrivateCaptureResult.Duplicate }, replay.Entries.Select(x => x.Disposition));
        var durable = await new OmsPostgresPrivateRecoveryReader(db.Source).ReadAsync(Account);
        Assert.True(durable.IsUsable); Assert.Equal(1, durable.AcceptedFactCount); Assert.Equal(2, durable.Observations.Count);
        Assert.Equal(replay.Entries.Select(x => x.Envelope.Observation.CaptureId).Order(), durable.Observations.Select(x => x.Envelope.Observation.CaptureId).Order());
    }

    [Fact]
    public async Task LostCaptureAckAfterRealCommitInvalidatesReplayWithoutResending()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var inner = new OmsPostgresPrivateEventCapture(db.Source);
        var capture = new OmsPrivateCaptureReplayTests.Capture { Handler = async (e, ct) => { await inner.CaptureAsync(e, ct); throw new IOException("synthetic lost capture ack"); } };
        await using var c = new OmsPrivateCaptureCoordinator(capture, new()); var s = await c.SwitchAsync(Account);
        using var runner = s.AcquireRunner(); var epoch = await c.BeginEpochAsync(s);
        await c.UpdateAsync(s, epoch, OmsPrivateStreamPhase.Recovering, OmsPrivateStreamProblem.AwaitingReconciliation);
        var w = await c.BeginReplayAsync(s, epoch); var receipt = await c.RegisterReceiptAsync(s, epoch, OmsPrivateCaptureReplayTests.Event(epoch));
        await Assert.ThrowsAsync<IOException>(() => c.TryCaptureReceiptAsync(receipt));
        var replay = await c.ReadReplayAsync(await c.SealReplayAsync(w));
        Assert.False(replay.CanReplay); Assert.Empty(replay.Entries); Assert.Contains(OmsPrivateReplayProblem.CaptureFailed, replay.Problems);
        var durable = await new OmsPostgresPrivateRecoveryReader(db.Source).ReadAsync(Account);
        Assert.True(durable.IsUsable); Assert.Equal(1, durable.AcceptedFactCount); Assert.Single(durable.Observations); Assert.Equal(1, capture.Calls);
    }

    [Fact]
    public async Task OptInAdapterRegistersReceiptsBeforeQueueAndCapturesRealPostgresFacts()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new OmsPostgresPrivateEventCapture(db.Source);
        var capture = new OmsPrivateCaptureReplayTests.Capture { Handler = async (e, ct) => { var value = await inner.CaptureAsync(e, ct); committed.TrySetResult(); return value; } };
        await using var c = new OmsPrivateCaptureCoordinator(capture, new());
        var s = await c.SwitchAsync(DeribitCaptureTestRig.Scope()); var socket = new DeribitCaptureTestRig.Socket(); using var stop = new CancellationTokenSource();
        var run = DeribitCaptureTestRig.Stream(() => socket).RunCaptureAsync(c, s, DeribitCaptureTestRig.Plan(), stop.Token);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (c.Status?.Phase != OmsPrivateStreamPhase.Recovering) await Task.Delay(10, deadline.Token);
            var w = await c.BeginReplayAsync(s, c.Status.Epoch); socket.Emit(DeribitCaptureTestRig.Trade());
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(10)); var batch = await c.ReadReplayAsync(await c.SealReplayAsync(w));
            Assert.True(batch.CanReplay); Assert.Single(batch.Entries); Assert.False(batch.VenueCoherent);
            var durable = await new OmsPostgresPrivateRecoveryReader(db.Source).ReadAsync(s.Scope);
            Assert.True(durable.IsUsable); Assert.Equal(1, durable.AcceptedFactCount);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
}
