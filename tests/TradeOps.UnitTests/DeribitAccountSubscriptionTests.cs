using System.Text.Json.Nodes;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;

public sealed class DeribitAccountSubscriptionTests
{
    private static DeribitPrivateCapturePlan WholePlan() => DeribitPrivateCapturePlan.ForAccountRecovery(123, false,
        Plan().Instruments.Values, Plan().Limits);
    private static string GlobalTrade(string instrument = "BTC-PERPETUAL")
    {
        var frame = JsonNode.Parse(Trade(instrument))!; frame["params"]!["channel"] = "user.trades.any.any.100ms"; return frame.ToJsonString();
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var stop = new CancellationTokenSource(Deadline);
        while (!predicate()) await Task.Delay(5, stop.Token);
    }

    [Fact]
    public async Task ActualPinnedGlobalAckProducesCurrentOwnerEvidenceAndParsesKnownNativeFacts()
    {
        var socket = new Socket(); var capture = new Capture();
        await using var coordinator = new OmsPrivateCaptureCoordinator(capture, new()); var session = await coordinator.SwitchAsync(Scope());
        using var stop = new CancellationTokenSource(); var run = Stream(() => socket).RunCaptureAsync(coordinator, session, WholePlan(), stop.Token);
        OmsPrivateAccountSubscriptions? proof = null;
        try
        {
            await Until(() => coordinator.Status?.Phase == OmsPrivateStreamPhase.Recovering);
            proof = coordinator.ReadAccountSubscriptions(session, coordinator.Status!.Epoch);
            Assert.NotNull(proof); Assert.Equal(new[] { "user.changes.any.any.raw", "user.orders.any.any.raw", "user.portfolio.any", "user.trades.any.any.100ms" }, proof.Channels);
            Assert.Equal(OmsAccountSubscriptionProfile.EconomicAccountV2, proof.Profile);
            Assert.True(coordinator.ValidateAccountSubscriptions(proof)); Assert.Equal(session.Scope, proof.Scope);
            socket.Emit(GlobalTrade()); var fact = await capture.Next(); Assert.Equal("BTC-PERPETUAL", fact.Facts.NativeInstrument);
            Assert.Equal("user.trades.any.any.100ms", fact.Observation.Channel);
            Assert.All(socket.Sent, x => Assert.Contains(x.Method,
                new[] { "public/auth", "private/get_account_summary", "public/set_heartbeat", "private/subscribe", "public/test" }));
        }
        finally { stop.Cancel(); await run.WaitAsync(Deadline); }
        Assert.False(coordinator.ValidateAccountSubscriptions(proof!));
    }

    [Fact]
    public async Task IncompleteNativeSubscriptionAckCannotIssueWholeAccountEvidence()
    {
        var socket = new Socket { OnSubscribe = (s, id, _) => s.Reply(id, new[] { "user.orders.any.any.raw" }) };
        await using var coordinator = new OmsPrivateCaptureCoordinator(new Capture(), new()); var session = await coordinator.SwitchAsync(Scope());
        using var stop = new CancellationTokenSource(); var run = Stream(() => socket).RunCaptureAsync(coordinator, session, WholePlan(), stop.Token);
        try
        {
            await Until(() => coordinator.Status?.LastFailure == OmsPrivateStreamProblem.ProtocolViolation);
            Assert.Null(coordinator.ReadAccountSubscriptions(session, coordinator.Status!.Epoch));
        }
        finally { stop.Cancel(); await run.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task UnknownInstrumentInGlobalStreamStopsInsteadOfBeingFilteredOut()
    {
        var socket = new Socket(); var capture = new Capture();
        await using var coordinator = new OmsPrivateCaptureCoordinator(capture, new()); var session = await coordinator.SwitchAsync(Scope());
        using var stop = new CancellationTokenSource(); var run = Stream(() => socket).RunCaptureAsync(coordinator, session, WholePlan(), stop.Token);
        try
        {
            await Until(() => coordinator.Status?.Phase == OmsPrivateStreamPhase.Recovering);
            var proof = coordinator.ReadAccountSubscriptions(session, coordinator.Status!.Epoch)!;
            socket.Emit(GlobalTrade("UNMAPPED-EXTERNAL-PRODUCT"));
            var failure = await Assert.ThrowsAsync<DeribitCaptureException>(() => run.WaitAsync(Deadline));
            Assert.Equal(OmsPrivateStreamProblem.UnsupportedInstrument, failure.Problem);
            Assert.False(coordinator.ValidateAccountSubscriptions(proof)); Assert.Empty(capture.Events);
        }
        finally { stop.Cancel(); }
    }

    [Theory]
    [InlineData("gap")] [InlineData("epoch")] [InlineData("runner")] [InlineData("scope")]
    public async Task GapEpochRunnerAndAccountChangesInvalidateOpaqueEvidence(string mode)
    {
        await using var coordinator = new OmsPrivateCaptureCoordinator(new Capture(), new()); var session = await coordinator.SwitchAsync(Scope());
        using var runner = session.AcquireRunner(); var epoch = await coordinator.BeginEpochAsync(session);
        await coordinator.UpdateAsync(session, epoch, OmsPrivateStreamPhase.Subscribing, OmsPrivateStreamProblem.AwaitingReconciliation);
        await coordinator.ConfirmAccountSubscriptionsAsync(session, runner, epoch,
            ["user.orders.any.any.raw", "user.trades.any.any.100ms"], DateTimeOffset.UtcNow);
        await coordinator.UpdateAsync(session, epoch, OmsPrivateStreamPhase.Recovering, OmsPrivateStreamProblem.AwaitingReconciliation);
        var proof = coordinator.ReadAccountSubscriptions(session, epoch)!; Assert.True(coordinator.ValidateAccountSubscriptions(proof));
        switch (mode)
        {
            case "gap": await coordinator.UpdateAsync(session, epoch, OmsPrivateStreamPhase.Recovering, OmsPrivateStreamProblem.TransportLost); break;
            case "epoch": await coordinator.BeginEpochAsync(session); break;
            case "runner": runner.Dispose(); using (session.AcquireRunner()) Assert.False(coordinator.ValidateAccountSubscriptions(proof)); break;
            default: await coordinator.SwitchAsync(Scope("other-account")); break;
        }
        Assert.False(coordinator.ValidateAccountSubscriptions(proof)); Assert.Null(coordinator.ReadAccountSubscriptions(session, epoch));
    }

    [Fact]
    public async Task ForeignRunnerAndDuplicateChannelClaimsCannotRegisterNativeCoverage()
    {
        await using var coordinator = new OmsPrivateCaptureCoordinator(new Capture(), new()); var session = await coordinator.SwitchAsync(Scope());
        using var runner = session.AcquireRunner(); using var fake = new CancellationTokenSource(); var epoch = await coordinator.BeginEpochAsync(session);
        await coordinator.UpdateAsync(session, epoch, OmsPrivateStreamPhase.Subscribing, OmsPrivateStreamProblem.AwaitingReconciliation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ConfirmAccountSubscriptionsAsync(session, fake, epoch,
            ["user.orders.any.any.raw", "user.trades.any.any.100ms"], DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.ConfirmAccountSubscriptionsAsync(session, runner, epoch,
            ["user.orders.any.any.raw", "user.orders.any.any.raw"], DateTimeOffset.UtcNow));
        Assert.Null(coordinator.ReadAccountSubscriptions(session, epoch));
    }

    [Fact]
    public async Task ExistingScopedCaptureNeverClaimsGlobalSubscriptionEvidence()
    {
        var socket = new Socket(); await using var coordinator = new OmsPrivateCaptureCoordinator(new Capture(), new());
        var session = await coordinator.SwitchAsync(Scope()); using var stop = new CancellationTokenSource();
        var run = Stream(() => socket).RunCaptureAsync(coordinator, session, Plan(), stop.Token);
        try { await Until(() => coordinator.Status?.Phase == OmsPrivateStreamPhase.Recovering); Assert.Null(coordinator.ReadAccountSubscriptions(session, coordinator.Status!.Epoch)); }
        finally { stop.Cancel(); await run.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task WholeAccountModeWithoutBoundedReplayDoesNotOpenTransport()
    {
        var socket = new Socket(); await using var coordinator = new OmsPrivateCaptureCoordinator(new Capture());
        var session = await coordinator.SwitchAsync(Scope());
        await Assert.ThrowsAsync<ArgumentException>(() => Stream(() => socket).RunCaptureAsync(coordinator, session, WholePlan()));
        Assert.Equal(0, socket.Connections); Assert.Empty(socket.Sent);
    }
}
