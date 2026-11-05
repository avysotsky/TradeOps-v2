using TradeOps.Application.Oms;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class OmsPrivateCaptureReplayTests
{
    internal sealed class Capture : IOmsPrivateEventCapture
    {
        internal int Calls;
        internal Func<OmsPrivateEventEnvelope, CancellationToken, Task<OmsPrivateCaptureResult>>? Handler;
        public async Task<OmsPrivateCaptureResult> CaptureAsync(OmsPrivateEventEnvelope envelope, CancellationToken cancellationToken = default)
        { Calls++; return Handler is null ? OmsPrivateCaptureResult.Inserted : await Handler(envelope, cancellationToken); }
    }
    internal sealed class Clock : TimeProvider
    {
        internal long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero).AddTicks(Ticks);
    }
    internal static OmsPrivateEventEnvelope Event(Guid epoch, Guid? capture = null) => Report() with {
        Observation = new(capture ?? Guid.NewGuid(), epoch, new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero),
            OmsPrivateCaptureSource.WebSocket, "user.orders.future.ETH.raw") };
    private sealed class Rig : IAsyncDisposable
    {
        internal readonly Capture Capture; internal readonly Clock Clock = new();
        internal readonly OmsPrivateCaptureCoordinator Coordinator;
        internal OmsPrivateCaptureSession Session = null!; internal Guid Epoch; internal IDisposable Runner = null!;
        private Rig(Capture capture, OmsPrivateReplayLimits limits) { Capture = capture; Coordinator = new(capture, limits, Clock); }
        internal static async Task<Rig> Open(OmsPrivateReplayLimits? limits = null, Capture? capture = null)
        {
            var r = new Rig(capture ?? new(), limits ?? new());
            r.Session = await r.Coordinator.SwitchAsync(Account); r.Runner = r.Session.AcquireRunner();
            r.Epoch = await r.Coordinator.BeginEpochAsync(r.Session);
            await r.Coordinator.UpdateAsync(r.Session, r.Epoch, OmsPrivateStreamPhase.Recovering, OmsPrivateStreamProblem.AwaitingReconciliation);
            return r;
        }
        internal async Task<OmsPrivateCaptureReceipt> Receipt(OmsPrivateEventEnvelope? item = null) =>
            await Coordinator.RegisterReceiptAsync(Session, Epoch, item ?? Event(Epoch));
        public async ValueTask DisposeAsync() { Runner.Dispose(); await Coordinator.DisposeAsync(); }
    }

    [Fact]
    public async Task HealthyStatusCannotCreateReplayBeforeNonemptyEpoch()
    {
        await using var c = new OmsPrivateCaptureCoordinator(new Capture(), new());
        var s = await c.SwitchAsync(Account); using var runner = s.AcquireRunner();
        await c.UpdateAsync(s, Guid.Empty, OmsPrivateStreamPhase.Recovering, OmsPrivateStreamProblem.AwaitingReconciliation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.BeginReplayAsync(s, Guid.Empty));
        var epoch = await c.BeginEpochAsync(s);
        await c.UpdateAsync(s, epoch, OmsPrivateStreamPhase.Recovering, OmsPrivateStreamProblem.AwaitingReconciliation);
        var w = await c.BeginReplayAsync(s, epoch); Assert.True((await c.ReadReplayAsync(await c.SealReplayAsync(w))).CanReplay);
    }

    [Fact]
    public async Task CompletePrefixPreservesObservationsButDoesNotProveVenueCoherence()
    {
        await using var r = await Rig.Open(); var window = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch);
        var a = await r.Receipt(); await r.Coordinator.TryCaptureReceiptAsync(a);
        var b = await r.Receipt(a.Envelope with { Observation = a.Envelope.Observation with { CaptureId = Guid.NewGuid() } });
        await r.Coordinator.TryCaptureReceiptAsync(b);
        var cut = await r.Coordinator.SealReplayAsync(window); var batch = await r.Coordinator.ReadReplayAsync(cut);
        Assert.True(batch.CanReplay); Assert.False(batch.VenueCoherent); Assert.False(batch.AccountReady);
        Assert.Equal(new long[] { 1, 2 }, batch.Entries.Select(x => x.ReceiptOrdinal));
        Assert.Equal(window.Id, batch.WindowId); Assert.Equal(Account, batch.Scope); Assert.Equal(r.Epoch, batch.Epoch);
        Assert.Equal(0, batch.AfterReceipt); Assert.Equal(2, batch.ThroughReceipt);
    }

    [Fact]
    public async Task PendingQueuedReceiptBlocksUntilCommitAndPostCutReceiptsAreExcluded()
    {
        await using var r = await Rig.Open(); var queuedBeforeBegin = await r.Receipt();
        var window = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch);
        var cut = await r.Coordinator.SealReplayAsync(window);
        var pending = await r.Coordinator.ReadReplayAsync(cut);
        Assert.False(pending.CanReplay); Assert.Contains(OmsPrivateReplayProblem.PendingCommits, pending.Problems); Assert.Empty(pending.Entries);
        await r.Coordinator.TryCaptureReceiptAsync(queuedBeforeBegin);
        var after = await r.Receipt(); await r.Coordinator.TryCaptureReceiptAsync(after);
        var complete = await r.Coordinator.ReadReplayAsync(cut);
        Assert.True(complete.CanReplay); Assert.Equal(queuedBeforeBegin.Ordinal, Assert.Single(complete.Entries).ReceiptOrdinal);
        Assert.Equal(2, r.Capture.Calls);
    }

    [Fact]
    public async Task ReceiptRegistrationDoesNotWaitForInflightDatabaseCommit()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new Capture { Handler = async (_, _) => { entered.TrySetResult(); await release.Task; return OmsPrivateCaptureResult.Inserted; } };
        await using var r = await Rig.Open(capture: capture); var w = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch);
        var a = await r.Receipt(); var commit = r.Coordinator.TryCaptureReceiptAsync(a);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var b = await r.Receipt().WaitAsync(TimeSpan.FromSeconds(1));
        var sealing = r.Coordinator.SealReplayAsync(w); Assert.False(sealing.IsCompleted);
        release.TrySetResult(); await commit; var cut = await sealing;
        Assert.Equal(2, cut.ThroughReceipt); Assert.False((await r.Coordinator.ReadReplayAsync(cut)).CanReplay);
        await r.Coordinator.TryCaptureReceiptAsync(b); Assert.True((await r.Coordinator.ReadReplayAsync(cut)).CanReplay);
    }

    [Theory]
    [InlineData("rows")] [InlineData("bytes")] [InlineData("pending")]
    public async Task OverflowInvalidatesReplayWithoutStoppingDurableCapture(string mode)
    {
        var limits = mode switch { "rows" => new OmsPrivateReplayLimits(MaximumObservations: 1),
            "bytes" => new OmsPrivateReplayLimits(MaximumBytes: 1024), _ => new OmsPrivateReplayLimits(MaximumPendingReceipts: 1) };
        await using var r = await Rig.Open(limits); var window = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch);
        var a = await r.Receipt(); var b = mode == "pending" ? await r.Receipt() : null;
        await r.Coordinator.TryCaptureReceiptAsync(a);
        b ??= await r.Receipt(); await r.Coordinator.TryCaptureReceiptAsync(b);
        var batch = await r.Coordinator.ReadReplayAsync(await r.Coordinator.SealReplayAsync(window));
        Assert.False(batch.CanReplay); Assert.Empty(batch.Entries); Assert.Contains(OmsPrivateReplayProblem.BufferOverflow, batch.Problems);
        Assert.Equal(2, r.Capture.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.Coordinator.BeginReplayAsync(r.Session, r.Epoch));
    }

    [Theory]
    [InlineData(OmsPrivateStreamProblem.TransportLost)] [InlineData(OmsPrivateStreamProblem.ProtocolViolation)]
    [InlineData(OmsPrivateStreamProblem.Collision)] [InlineData(OmsPrivateStreamProblem.Renewal)]
    public async Task GapIsStickyEvenAfterHealthyStatusIsRestored(OmsPrivateStreamProblem problem)
    {
        await using var r = await Rig.Open(); var w = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch);
        await r.Coordinator.TryCaptureReceiptAsync(await r.Receipt()); var cut = await r.Coordinator.SealReplayAsync(w);
        await r.Coordinator.UpdateAsync(r.Session, r.Epoch, OmsPrivateStreamPhase.Recovering, problem);
        await r.Coordinator.UpdateAsync(r.Session, r.Epoch, OmsPrivateStreamPhase.Recovering, OmsPrivateStreamProblem.AwaitingReconciliation);
        var b = await r.Coordinator.ReadReplayAsync(cut); Assert.False(b.CanReplay); Assert.Empty(b.Entries);
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.Coordinator.BeginReplayAsync(r.Session, r.Epoch));
    }

    [Theory]
    [InlineData("epoch")] [InlineData("account")] [InlineData("stop")] [InlineData("runner")]
    public async Task CutCannotSurviveBindingOrRunnerReplacement(string mode)
    {
        await using var r = await Rig.Open(); var w = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch);
        await r.Coordinator.TryCaptureReceiptAsync(await r.Receipt()); var cut = await r.Coordinator.SealReplayAsync(w);
        if (mode == "epoch") await r.Coordinator.BeginEpochAsync(r.Session);
        else if (mode == "account") await r.Coordinator.SwitchAsync(new(new("other"), Account.Venue, Account.Environment, Account.Account));
        else if (mode == "stop") await r.Coordinator.DisposeAsync();
        else { r.Runner.Dispose(); r.Runner = r.Session.AcquireRunner(); }
        var b = await r.Coordinator.ReadReplayAsync(cut); Assert.False(b.CanReplay); Assert.Empty(b.Entries);
    }

    [Fact]
    public async Task OutOfOrderCommitFailsClosedAndNewEpochCanStartFresh()
    {
        await using var r = await Rig.Open(); var w = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch);
        var a = await r.Receipt(); var b = await r.Receipt(); await r.Coordinator.TryCaptureReceiptAsync(b); await r.Coordinator.TryCaptureReceiptAsync(a);
        Assert.Contains(OmsPrivateReplayProblem.ReceiptOrder, (await r.Coordinator.ReadReplayAsync(await r.Coordinator.SealReplayAsync(w))).Problems);
        r.Epoch = await r.Coordinator.BeginEpochAsync(r.Session);
        await r.Coordinator.UpdateAsync(r.Session, r.Epoch, OmsPrivateStreamPhase.Recovering, OmsPrivateStreamProblem.AwaitingReconciliation);
        var next = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch);
        await r.Coordinator.TryCaptureReceiptAsync(await r.Receipt()); Assert.True((await r.Coordinator.ReadReplayAsync(await r.Coordinator.SealReplayAsync(next))).CanReplay);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CollisionOrUnknownCaptureReturnNeverProducesReplay(bool unknown)
    {
        var capture = new Capture { Handler = (_, _) => Task.FromResult(unknown ? (OmsPrivateCaptureResult)999 : OmsPrivateCaptureResult.Collision) };
        await using var r = await Rig.Open(capture: capture); var w = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch);
        await r.Coordinator.TryCaptureReceiptAsync(await r.Receipt()); Assert.False((await r.Coordinator.ReadReplayAsync(await r.Coordinator.SealReplayAsync(w))).CanReplay);
    }

    [Fact]
    public async Task ExpirationIsMonotonicAndSupersededCutsAreRejected()
    {
        await using var r = await Rig.Open(new(MaximumDuration: TimeSpan.FromSeconds(1)));
        var old = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch); var oldCut = await r.Coordinator.SealReplayAsync(old);
        var next = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch);
        Assert.Contains(OmsPrivateReplayProblem.Superseded, (await r.Coordinator.ReadReplayAsync(oldCut)).Problems);
        r.Clock.Ticks += TimeSpan.FromSeconds(2).Ticks;
        Assert.Contains(OmsPrivateReplayProblem.Expired, (await r.Coordinator.ReadReplayAsync(await r.Coordinator.SealReplayAsync(next))).Problems);
    }

    [Fact]
    public async Task UntrackedLegacyCapturePoisonsOptInReplayButStillCommits()
    {
        await using var r = await Rig.Open(); var w = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch);
        await r.Coordinator.TryCaptureAsync(r.Session, r.Epoch, Event(r.Epoch));
        var b = await r.Coordinator.ReadReplayAsync(await r.Coordinator.SealReplayAsync(w));
        Assert.Contains(OmsPrivateReplayProblem.UntrackedCapture, b.Problems); Assert.Equal(1, r.Capture.Calls);
    }

    [Fact]
    public async Task ReceiptsCannotBeReusedOrTransferredAcrossCoordinators()
    {
        await using var r = await Rig.Open(); await using var other = await Rig.Open(); var receipt = await r.Receipt();
        await Assert.ThrowsAsync<ArgumentException>(() => other.Coordinator.TryCaptureReceiptAsync(receipt));
        await r.Coordinator.TryCaptureReceiptAsync(receipt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => r.Coordinator.TryCaptureReceiptAsync(receipt));
        var w = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch); var cut = await r.Coordinator.SealReplayAsync(w);
        await Assert.ThrowsAsync<ArgumentException>(() => other.Coordinator.ReadReplayAsync(cut));
    }

    [Fact]
    public async Task InvalidReceiptPoisonsReplayAndDisabledReplayDoesNoCapture()
    {
        await using var r = await Rig.Open(); var w = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch);
        await Assert.ThrowsAsync<ArgumentException>(() => r.Receipt(Event(r.Epoch) with { Scope = new(new("foreign"), Account.Venue, Account.Environment, Account.Account) }));
        Assert.Contains(OmsPrivateReplayProblem.InvalidObservation, (await r.Coordinator.ReadReplayAsync(await r.Coordinator.SealReplayAsync(w))).Problems);
        await using var disabled = new OmsPrivateCaptureCoordinator(new Capture());
        await Assert.ThrowsAsync<InvalidOperationException>(() => disabled.BeginReplayAsync(r.Session, r.Epoch)); Assert.Equal(0, r.Capture.Calls);
    }
}
