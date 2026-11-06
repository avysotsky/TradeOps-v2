using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;

public sealed class OmsPrivateAccountRevisionTests
{
    [Fact]
    public async Task ExternalIngressBlocksQualificationUntilDurableDrainWithoutInventingWsReceipts()
    {
        await using var setup=await Setup.Open();var c=setup.Coordinator;
        var w=await c.BeginReplayAsync(setup.Session,setup.Epoch);var cut=await c.SealReplayAsync(w);
        var old=c.TryCreateAccountRevision(cut,setup.Subscriptions)!;c.AbandonReplay(w);
        using var ingress=c.BeginExternalIngress(setup.Session);
        Assert.False(c.IsAccountRevisionCurrent(old));
        var next=await c.BeginReplayAsync(setup.Session,setup.Epoch);var nextCut=await c.SealReplayAsync(next);
        Assert.Null(c.TryCreateAccountRevision(nextCut,setup.Subscriptions));
        ingress.Complete();
        Assert.Null(c.TryCreateAccountRevision(nextCut,setup.Subscriptions));
        next=await c.BeginReplayAsync(setup.Session,setup.Epoch);nextCut=await c.SealReplayAsync(next);
        var current=c.TryCreateAccountRevision(nextCut,setup.Subscriptions)!;
        Assert.NotNull(current);Assert.Equal(0,current.ThroughReceipt);Assert.True(c.IsAccountRevisionCurrent(current));
        Assert.False(c.IsAccountRevisionCurrent(old));
    }

    [Fact]
    public async Task AbandonedExternalIngressFaultsBaselineAndOtherPendingIngressCannotBeClearedEarly()
    {
        await using var setup=await Setup.Open();var c=setup.Coordinator;
        using var first=c.BeginExternalIngress(setup.Session);using var second=c.BeginExternalIngress(setup.Session);
        first.Complete();var w=await c.BeginReplayAsync(setup.Session,setup.Epoch);var cut=await c.SealReplayAsync(w);
        Assert.Null(c.TryCreateAccountRevision(cut,setup.Subscriptions));
        second.Dispose();Assert.False(c.ValidateAccountSubscriptions(setup.Subscriptions));
        Assert.Null(c.TryCreateAccountRevision(cut,setup.Subscriptions));
    }

    [Fact]
    public async Task WsEpochRestartCannotErasePendingNativeIngress()
    {
        await using var setup=await Setup.Open();var c=setup.Coordinator;
        using var ingress=c.BeginExternalIngress(setup.Session);
        var epoch=await c.BeginEpochAsync(setup.Session);
        await c.UpdateAsync(setup.Session,epoch,OmsPrivateStreamPhase.Subscribing,OmsPrivateStreamProblem.AwaitingReconciliation);
        await c.ConfirmAccountSubscriptionsAsync(setup.Session,setup.Runner,epoch,
            ["user.orders.any.any.raw","user.trades.any.any.100ms"],DateTimeOffset.UtcNow);
        await c.UpdateAsync(setup.Session,epoch,OmsPrivateStreamPhase.Recovering,OmsPrivateStreamProblem.AwaitingReconciliation);
        var subscriptions=c.ReadAccountSubscriptions(setup.Session,epoch)!;
        var w=await c.BeginReplayAsync(setup.Session,epoch);var cut=await c.SealReplayAsync(w);
        Assert.Null(c.TryCreateAccountRevision(cut,subscriptions));ingress.Complete();
        Assert.Null(c.TryCreateAccountRevision(cut,subscriptions));
        w=await c.BeginReplayAsync(setup.Session,epoch);cut=await c.SealReplayAsync(w);
        Assert.NotNull(c.TryCreateAccountRevision(cut,subscriptions));
    }
    [Fact]
    public async Task DrainedCutRevisionCannotBeImportedAndNewIngressInvalidatesItBeforeCommit()
    {
        await using var setup = await Setup.Open(); var c = setup.Coordinator;
        var window = await c.BeginReplayAsync(setup.Session, setup.Epoch);
        var cut = await c.SealReplayAsync(window); Assert.True((await c.ReadReplayAsync(cut)).CanReplay);
        var revision = c.TryCreateAccountRevision(cut, setup.Subscriptions)!; Assert.NotNull(revision);
        c.AbandonReplay(window); Assert.True(c.IsAccountRevisionCurrent(revision));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<OmsPrivateAccountRevision>(JsonSerializer.Serialize(revision)));
        var pending = await c.RegisterReceiptAsync(setup.Session, setup.Epoch, setup.Event());
        Assert.False(c.IsAccountRevisionCurrent(revision)); // no database commit or capture completion yet
        await c.TryCaptureReceiptAsync(pending); Assert.False(c.IsAccountRevisionCurrent(revision));
        var next = await c.BeginReplayAsync(setup.Session, setup.Epoch); var nextCut = await c.SealReplayAsync(next);
        var current = c.TryCreateAccountRevision(nextCut, setup.Subscriptions)!;
        Assert.NotNull(current); Assert.Equal(1, current.ThroughReceipt); Assert.True(c.IsAccountRevisionCurrent(current));
    }

    [Fact]
    public async Task UntrackedCaptureCannotLeavePreviouslyIssuedRevisionUsable()
    {
        await using var setup = await Setup.Open(); var c = setup.Coordinator;
        var window = await c.BeginReplayAsync(setup.Session, setup.Epoch); var cut = await c.SealReplayAsync(window);
        var revision = c.TryCreateAccountRevision(cut, setup.Subscriptions)!; c.AbandonReplay(window);
        Assert.True(c.IsAccountRevisionCurrent(revision));
        await c.TryCaptureAsync(setup.Session, setup.Epoch, setup.Event());
        Assert.False(c.IsAccountRevisionCurrent(revision)); Assert.False(c.ValidateAccountSubscriptions(setup.Subscriptions));
    }

    [Fact]
    public async Task ActualGlobalProducerAndPostgresRunRetainArchiveButCannotRestoreLiveRevision()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        await using var queryPorts = await DeribitRecoveryReplayGateTests.Rig.Open();
        await using var coordinator = new OmsPrivateCaptureCoordinator(new OmsPostgresPrivateEventCapture(db.Source), new());
        var session = await coordinator.SwitchAsync(Scope()); var socket = new Socket(); using var stop = new CancellationTokenSource();
        var plan = DeribitPrivateCapturePlan.ForAccountRecovery(123, false, Plan().Instruments.Values, Plan().Limits);
        var capture = Stream(() => socket).RunCaptureAsync(coordinator, session, plan, stop.Token);
        try
        {
            using var ready = new CancellationTokenSource(Deadline);
            while (coordinator.Status?.Phase != OmsPrivateStreamPhase.Recovering) await Task.Delay(5, ready.Token);
            var q = queryPorts.Request(); q = q with { Query = q.Query with { Epoch = coordinator.Status.Epoch } };
            var store = new OmsPostgresRecoveryRunStore(db.Source);
            var engine = new DeribitRecoveryRunEngine(store, new OmsPostgresRecoveryLedgerReader(db.Source), queryPorts, queryPorts,
                new DeribitRecoveryAccountAssessmentTests.Accounts());
            var result = await engine.RunAsync(q, DeribitRecoveryAccountAssessmentTests.Request(q), coordinator, session);
            var replay = result.Assessment.Account.Replay!;
            Assert.True(replay.LocalReplayValidated); Assert.Equal(0, replay.BufferedObservations);
            Assert.NotNull(replay.Revision); Assert.True(coordinator.IsAccountRevisionCurrent(replay.Revision));
            Assert.NotNull(replay.BeganAtUtc); Assert.NotNull(replay.SealedAtUtc); Assert.Equal(replay.AfterReceipt, replay.ThroughReceipt);
            var saved = (await store.ReadResultAsync(session.Scope, result.Start.RunId))!;
            Assert.Null(saved.Assessment.Account.Replay!.Revision); Assert.Equal(replay.ThroughReceipt, saved.Assessment.Account.Replay.ThroughReceipt);
            Assert.False((await store.ReadHeadAsync(session.Scope))!.CanIncreaseRisk);
        }
        finally { stop.Cancel(); await capture.WaitAsync(Deadline); }
    }

    internal sealed class Setup : IAsyncDisposable
    {
        internal readonly OmsPrivateCaptureCoordinator Coordinator = new(new Capture(), new());
        internal OmsPrivateCaptureSession Session = null!; internal IDisposable Runner = null!;
        internal Guid Epoch; internal OmsPrivateAccountSubscriptions Subscriptions = null!;
        internal static async Task<Setup> Open()
        {
            var s = new Setup(); s.Session = await s.Coordinator.SwitchAsync(Scope()); s.Runner = s.Session.AcquireRunner();
            s.Epoch = await s.Coordinator.BeginEpochAsync(s.Session);
            await s.Coordinator.UpdateAsync(s.Session, s.Epoch, OmsPrivateStreamPhase.Subscribing, OmsPrivateStreamProblem.AwaitingReconciliation);
            await s.Coordinator.ConfirmAccountSubscriptionsAsync(s.Session, s.Runner, s.Epoch,
                ["user.orders.any.any.raw", "user.trades.any.any.100ms"], DateTimeOffset.UtcNow);
            await s.Coordinator.UpdateAsync(s.Session, s.Epoch, OmsPrivateStreamPhase.Recovering, OmsPrivateStreamProblem.AwaitingReconciliation);
            s.Subscriptions = s.Coordinator.ReadAccountSubscriptions(s.Session, s.Epoch)!; return s;
        }
        internal OmsPrivateEventEnvelope Event() => OmsPrivateCaptureReplayTests.Event(Epoch) with { Scope = Session.Scope };
        public async ValueTask DisposeAsync() { Runner.Dispose(); await Coordinator.DisposeAsync(); }
    }
}
