using TradeOps.Application.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;

public sealed class OmsPrivateCaptureCoordinatorTests
{
    [Fact]
    public async Task AccountSwitch_WaitsForCommit_ThenRejectsAllStaleCapturesAndStatusUpdates()
    {
        var capture = new Capture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.BeforeCommit = async (_, _) => { entered.TrySetResult(); await release.Task; return OmsPrivateCaptureResult.Inserted; };
        await using var coordinator = new OmsPrivateCaptureCoordinator(capture);
        var first = await coordinator.SwitchAsync(Scope());
        var epoch = await coordinator.BeginEpochAsync(first);
        var envelope = OmsPrivateEventContractTests.Execution(first.Scope) with
        { Observation = OmsPrivateEventContractTests.Execution().Observation with { StreamEpoch = epoch } };
        var commit = coordinator.TryCaptureAsync(first, epoch, envelope);
        await entered.Task.WaitAsync(Deadline);
        var switching = coordinator.SwitchAsync(Scope("other"));
        Assert.False(switching.IsCompleted);
        release.TrySetResult();
        Assert.Equal(OmsPrivateCaptureResult.Inserted, await commit);
        var second = await switching.WaitAsync(Deadline);
        Assert.True(first.Cancellation.IsCancellationRequested);
        Assert.Null(await coordinator.TryCaptureAsync(first, epoch, envelope));
        Assert.False(await coordinator.UpdateAsync(first, epoch, OmsPrivateStreamPhase.Recovering, OmsPrivateStreamProblem.Collision));
        Assert.Equal(second.Scope, coordinator.Status!.Scope);
        Assert.Single(capture.Events);
    }

    [Fact]
    public async Task NewEpoch_RejectsOldEpochAndWrongScope_AndRetainsRecoveryReason()
    {
        var capture = new Capture();
        await using var coordinator = new OmsPrivateCaptureCoordinator(capture);
        var session = await coordinator.SwitchAsync(Scope());
        var old = await coordinator.BeginEpochAsync(session);
        await coordinator.UpdateAsync(session, old, OmsPrivateStreamPhase.Recovering, OmsPrivateStreamProblem.TransportLost);
        var epoch = await coordinator.BeginEpochAsync(session);
        Assert.NotEqual(old, epoch);
        var envelope = OmsPrivateEventContractTests.Execution(session.Scope);
        envelope = envelope with { Observation = envelope.Observation with { StreamEpoch = old } };
        Assert.Null(await coordinator.TryCaptureAsync(session, old, envelope));
        envelope = envelope with { Observation = envelope.Observation with { StreamEpoch = epoch }, Scope = Scope("other") };
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.TryCaptureAsync(session, epoch, envelope));
        Assert.Equal(OmsPrivateStreamProblem.TransportLost, coordinator.Status!.LastFailure);
        Assert.Empty(capture.Events);
    }

    [Fact]
    public async Task SessionRunner_IsExclusiveAndRevokedByAccountSwitch()
    {
        await using var coordinator = new OmsPrivateCaptureCoordinator(new Capture());
        var first = await coordinator.SwitchAsync(Scope());
        using (first.AcquireRunner()) Assert.Throws<InvalidOperationException>(() => first.AcquireRunner());
        using (first.AcquireRunner()) { }
        await coordinator.SwitchAsync(Scope("other"));
        Assert.Throws<OperationCanceledException>(() => first.AcquireRunner());
    }
}
