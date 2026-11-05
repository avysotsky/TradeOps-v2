using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitRecoveryReplayGateTests
{
    internal sealed class Rig : IOmsPrivateRecoveryReader, IDeribitRecoveryQueries, IDeribitKnownOrderReader, IAsyncDisposable
    {
        internal readonly OmsPrivateCaptureReplayTests.Capture Capture;
        internal readonly OmsPrivateCaptureCoordinator Coordinator;
        internal OmsPrivateCaptureSession Session = null!; internal Guid Epoch; internal IDisposable Runner = null!;
        internal Func<CancellationToken, Task>? SnapshotHook, QueryHook;
        internal int SnapshotCalls, QueryCalls;
        internal bool SnapshotProblem;
        private Rig(OmsPrivateCaptureReplayTests.Capture capture, OmsPrivateReplayLimits? limits, TimeProvider? replayTime)
        { Capture = capture; Coordinator = new(capture, limits ?? new(), replayTime); }
        internal static async Task<Rig> Open(OmsPrivateCaptureReplayTests.Capture? capture = null, OmsPrivateReplayLimits? limits = null, TimeProvider? time = null)
        {
            var r = new Rig(capture ?? new(), limits, time);
            r.Session = await r.Coordinator.SwitchAsync(Account); r.Runner = r.Session.AcquireRunner();
            r.Epoch = await r.Coordinator.BeginEpochAsync(r.Session);
            await r.Coordinator.UpdateAsync(r.Session, r.Epoch, OmsPrivateStreamPhase.Recovering, OmsPrivateStreamProblem.AwaitingReconciliation);
            return r;
        }
        internal DeribitRecoveryEvidenceGate Gate() => new(this, this, this);
        internal DeribitRecoveryGateRequest Request() => new(new(Account, Epoch, "ETH-PERPETUAL", DateTimeOffset.UtcNow.AddHours(-1),
            DateTimeOffset.UtcNow.AddSeconds(-1), TimeSpan.FromSeconds(1)), "ETH", OmsPrivateAmountUnit.Usd, "USD");
        internal Task<DeribitRecoveryReplayAssessment> Assess(DeribitReplayDrainOptions? options = null, CancellationToken ct = default) =>
            Gate().AssessWithReplayAsync(Request(), Coordinator, Session, options, ct);
        internal Task<OmsPrivateCaptureReceipt> Receipt(OmsPrivateEventEnvelope? envelope = null) =>
            Coordinator.RegisterReceiptAsync(Session, Epoch, envelope ?? OmsPrivateCaptureReplayTests.Event(Epoch));
        public async Task<OmsPrivateRecoveryReadResult> ReadAsync(AccountKey scope, int maximumRows = 10000, CancellationToken cancellationToken = default)
        {
            SnapshotCalls++; if (SnapshotHook is not null) await SnapshotHook(cancellationToken);
            return new(scope, Guid.NewGuid(), DateTimeOffset.UtcNow, 0, Array.Empty<OmsPrivateRecoveryObservation>(),
                SnapshotProblem ? new[] { OmsPrivateRecoveryReadProblem.IntegrityIncident } : Array.Empty<OmsPrivateRecoveryReadProblem>());
        }
        public async Task<DeribitRecoveryQueryResult> CollectAsync(DeribitRecoveryQuery q, CancellationToken ct = default)
        {
            QueryCalls++; var started = DateTimeOffset.UtcNow; if (QueryHook is not null) await QueryHook(ct);
            return new(q.Scope, q.Epoch, q.NativeInstrument, "ETH", DateTimeOffset.FromUnixTimeMilliseconds((q.FromUtc-q.Overlap).ToUnixTimeMilliseconds()),
                DateTimeOffset.FromUnixTimeMilliseconds(q.ThroughUtc.ToUnixTimeMilliseconds()), started, DateTimeOffset.UtcNow,
                new[] { "open", "orders-recent", "orders-historical", "trades-recent", "trades-historical" }.Select(x => new DeribitRecoverySourceRead(x, 1, true)).ToArray(),
                Array.Empty<OmsPrivateEventEnvelope>(), Array.Empty<DeribitRecoveryQueryProblem>());
        }
        public Task<DeribitKnownOrderReadResult> ReadAsync(DeribitKnownOrderReadRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("Empty retained scope must not invent addressed lookups.");
        public async ValueTask DisposeAsync() { Runner.Dispose(); await Coordinator.DisposeAsync(); }
    }

    [Fact]
    public async Task WindowCoversSnapshotAndQueryButRemovesOnlyLocalBufferBlocker()
    {
        await using var r = await Rig.Open();
        r.SnapshotHook = async _ => { await r.Coordinator.TryCaptureReceiptAsync(await r.Receipt()); };
        r.QueryHook = async _ => { await r.Coordinator.TryCaptureReceiptAsync(await r.Receipt()); };
        var result = await r.Assess(); Assert.True(result.LocalReplayValidated); Assert.Equal(2, result.BufferedObservations);
        Assert.Equal(2, result.SelectedInstrumentObservations); Assert.Empty(result.ReplayProblems);
        Assert.DoesNotContain(DeribitRecoveryEvidenceBlocker.BufferedReplayUnavailable, result.Evidence.Blockers);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.CoherentCutUnavailable, result.Evidence.Blockers);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.ExecutionLedgerCoverageUnproven, result.Evidence.Blockers);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.OrderHistoryIncomplete, result.Evidence.Blockers);
        Assert.False(result.Evidence.CanRebuildOrders); Assert.False(result.Evidence.AccountReady);
    }

    [Fact]
    public async Task PendingPrefixIsAwaitedThenValidated()
    {
        await using var r = await Rig.Open(); OmsPrivateCaptureReceipt? receipt = null;
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        r.QueryHook = async _ => { receipt = await r.Receipt(); registered.TrySetResult(); };
        var assessment = r.Assess(new(MaximumWait: TimeSpan.FromSeconds(2)));
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(assessment.IsCompleted);
        await r.Coordinator.TryCaptureReceiptAsync(receipt!);
        var result = await assessment; Assert.True(result.LocalReplayValidated); Assert.Equal(1, result.BufferedObservations);
    }

    [Fact]
    public async Task PendingDrainTimeoutReturnsNoFactsAndReleasesOwnedWindow()
    {
        await using var r = await Rig.Open(); OmsPrivateCaptureReceipt? receipt = null;
        r.QueryHook = async _ => receipt = await r.Receipt();
        var result = await r.Assess(new(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(5)));
        Assert.False(result.LocalReplayValidated); Assert.Equal(0, result.BufferedObservations);
        Assert.Contains(OmsPrivateReplayProblem.DrainTimeout, result.ReplayProblems);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.BufferedReplayDrainTimedOut, result.Evidence.Blockers);
        var next = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch);
        await r.Coordinator.TryCaptureReceiptAsync(receipt!);
        Assert.True((await r.Coordinator.ReadReplayAsync(await r.Coordinator.SealReplayAsync(next))).CanReplay);
    }

    [Fact]
    public async Task SealTimeoutDoesNotWaitForOrCancelIndependentDurableCapture()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new OmsPrivateCaptureReplayTests.Capture { Handler = async (_, _) => { entered.TrySetResult(); await release.Task; return OmsPrivateCaptureResult.Inserted; } };
        await using var r = await Rig.Open(capture); Task<OmsPrivateCaptureResult?>? committing = null;
        r.QueryHook = async _ => { committing = r.Coordinator.TryCaptureReceiptAsync(await r.Receipt()); await entered.Task; };
        try
        {
            var result = await r.Assess(new(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(5))).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.LocalReplayValidated); Assert.Contains(OmsPrivateReplayProblem.DrainTimeout, result.ReplayProblems);
            Assert.False(committing!.IsCompleted);
        }
        finally { release.TrySetResult(); if (committing is not null) await committing; }
        Assert.Equal(1, capture.Calls);
    }

    [Theory]
    [InlineData("epoch")] [InlineData("scope")] [InlineData("gap")] [InlineData("runner")]
    public async Task BindingLossDuringQueryCannotClearBufferBlocker(string mode)
    {
        await using var r = await Rig.Open();
        r.QueryHook = async _ => {
            if (mode == "epoch") await r.Coordinator.BeginEpochAsync(r.Session);
            else if (mode == "scope") await r.Coordinator.SwitchAsync(new(new("foreign"), Account.Venue, Account.Environment, Account.Account));
            else if (mode == "gap") await r.Coordinator.UpdateAsync(r.Session, r.Epoch, OmsPrivateStreamPhase.Recovering, OmsPrivateStreamProblem.TransportLost);
            else { r.Runner.Dispose(); r.Runner = r.Session.AcquireRunner(); }
        };
        var result = await r.Assess(); Assert.False(result.LocalReplayValidated);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.BufferedReplayUnavailable, result.Evidence.Blockers);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.BufferedReplayInvalid, result.Evidence.Blockers); Assert.Equal(0, result.BufferedObservations);
    }

    [Fact]
    public async Task SupersedingWindowSurvivesOldAssessmentCleanup()
    {
        await using var r = await Rig.Open(); OmsPrivateReplayWindow? other = null;
        r.QueryHook = async _ => other = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch);
        var result = await r.Assess(); Assert.False(result.LocalReplayValidated); Assert.Contains(OmsPrivateReplayProblem.Superseded, result.ReplayProblems);
        Assert.True((await r.Coordinator.ReadReplayAsync(await r.Coordinator.SealReplayAsync(other!))).CanReplay);
    }

    [Theory]
    [InlineData("exception")] [InlineData("cancel-query")] [InlineData("cancel-drain")]
    public async Task FailureAndCallerCancellationPropagateAndReleaseWindow(string mode)
    {
        await using var r = await Rig.Open(); using var stop = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        r.QueryHook = async ct => {
            if (mode == "exception") throw new IOException("synthetic read failure");
            if (mode == "cancel-query") { stop.Cancel(); ct.ThrowIfCancellationRequested(); }
            await r.Receipt(); ready.TrySetResult();
        };
        var pending = r.Assess(ct: stop.Token);
        if (mode == "cancel-drain") { await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); stop.Cancel(); }
        if (mode == "exception") await Assert.ThrowsAsync<IOException>(() => pending);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var next = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch); Assert.NotNull(next);
    }

    [Fact]
    public async Task InvalidInputDoesNotSupersedeAnExistingWindow()
    {
        await using var r = await Rig.Open(); var own = await r.Coordinator.BeginReplayAsync(r.Session, r.Epoch);
        await Assert.ThrowsAsync<ArgumentException>(() => r.Gate().AssessWithReplayAsync(r.Request() with {
            Query = r.Request().Query with { Epoch = Guid.Empty } }, r.Coordinator, r.Session));
        await Assert.ThrowsAsync<ArgumentException>(() => r.Assess(new(MaximumWait: TimeSpan.Zero)));
        Assert.Equal(0, r.SnapshotCalls); Assert.True((await r.Coordinator.ReadReplayAsync(await r.Coordinator.SealReplayAsync(own))).CanReplay);
    }

    [Fact]
    public async Task SnapshotProblemsRemainBlockingEvenWithValidQuietReplay()
    {
        await using var r = await Rig.Open(); r.SnapshotProblem = true;
        var result = await r.Assess(); Assert.True(result.LocalReplayValidated);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.SnapshotUnusable, result.Evidence.Blockers);
        Assert.False(result.Evidence.CanRebuildOrders); Assert.Equal(0, r.QueryCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OtherInstrumentPrefixIsSeparateAndSelectedMetadataMustMatch(bool wrongSelectedMetadata)
    {
        await using var r = await Rig.Open(); var e = OmsPrivateCaptureReplayTests.Event(r.Epoch);
        e = e with { Facts = e.Facts with { Currency = "BTC", NativeInstrument = wrongSelectedMetadata ? "ETH-PERPETUAL" : "BTC-PERPETUAL" } };
        r.QueryHook = async _ => await r.Coordinator.TryCaptureReceiptAsync(await r.Receipt(e));
        var result = await r.Assess(); Assert.Equal(!wrongSelectedMetadata, result.LocalReplayValidated);
        Assert.Equal(0, result.SelectedInstrumentObservations); Assert.False(result.Evidence.AccountReady);
    }

    [Fact]
    public async Task OverflowAndExpiryRemainUnusableInGate()
    {
        var clock = new OmsPrivateCaptureReplayTests.Clock();
        await using var r = await Rig.Open(limits: new(MaximumObservations: 1, MaximumDuration: TimeSpan.FromSeconds(1)), time: clock);
        r.QueryHook = async _ => {
            await r.Coordinator.TryCaptureReceiptAsync(await r.Receipt());
            await r.Coordinator.TryCaptureReceiptAsync(await r.Receipt());
            clock.Ticks += TimeSpan.FromSeconds(2).Ticks;
        };
        var result = await r.Assess(); Assert.False(result.LocalReplayValidated);
        Assert.Contains(OmsPrivateReplayProblem.BufferOverflow, result.ReplayProblems); Assert.Contains(OmsPrivateReplayProblem.Expired, result.ReplayProblems);
    }
}
