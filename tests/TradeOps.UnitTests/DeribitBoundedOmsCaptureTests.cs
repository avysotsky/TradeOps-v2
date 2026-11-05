using TradeOps.Application.Oms;
using TradeOps.DeribitMainnetEthQualification;
using TradeOps.Domain.Oms;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitBoundedOmsCaptureTests
{
    [Fact]
    public async Task OwnFillStopsBeforeCaptureCommitCompletes()
    {
        var completion = new TaskCompletionSource<OmsPrivateCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new Sink(completion.Task); var monitor = new BoundedOmsCapture(inner, Account); monitor.TrackLabel("owner-label");
        var envelope = Report("open", .25m) with { Facts = Report("open", .25m).Facts with { ClientLabel = "owner-label" } };
        var capturing = monitor.CaptureAsync(envelope);
        Assert.True(monitor.FillObserved); Assert.False(capturing.IsCompleted);
        completion.SetResult(OmsPrivateCaptureResult.Inserted); await capturing;
    }

    [Fact]
    public async Task SameNativeIdInAnotherAccountDoesNotSetOwnFillSignal()
    {
        var monitor = new BoundedOmsCapture(new Sink(Task.FromResult(OmsPrivateCaptureResult.Inserted)), Account);
        monitor.TrackNativeId("order-1");
        await monitor.CaptureAsync(Fill() with { Scope = new AccountKey(Account.Tenant, Account.Venue, Account.Environment, new("foreign")) });
        Assert.False(monitor.FillObserved);
    }

    [Fact]
    public async Task OpenZeroFillReportDoesNotStopQualification()
    {
        var monitor = new BoundedOmsCapture(new Sink(Task.FromResult(OmsPrivateCaptureResult.Inserted)), Account);
        monitor.TrackNativeId("order-1"); await monitor.CaptureAsync(Report("open", 0)); Assert.False(monitor.FillObserved);
    }

    private sealed class Sink(Task<OmsPrivateCaptureResult> result) : IOmsPrivateEventCapture
    { public Task<OmsPrivateCaptureResult> CaptureAsync(OmsPrivateEventEnvelope envelope, CancellationToken cancellationToken = default) => result; }
}
