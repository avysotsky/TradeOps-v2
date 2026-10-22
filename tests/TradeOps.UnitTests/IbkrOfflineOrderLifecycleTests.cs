using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Ibkr;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class IbkrOfflineOrderLifecycleTests
{
    private const string Coid = "to-test-order";
    private const string BrokerId = "synthetic-123";

    [Fact]
    public void DeterministicCoid_IsStableAndSeparatesBusinessKeys()
    {
        var one = IbkrOfflineOrderLifecycle.CreateDeterministicCoid("portfolio", "order-17");
        Assert.Equal(one, IbkrOfflineOrderLifecycle.CreateDeterministicCoid("portfolio", "order-17"));
        Assert.StartsWith("to-", one);
        Assert.Equal(27, one.Length);
        Assert.NotEqual(one, IbkrOfflineOrderLifecycle.CreateDeterministicCoid("portfolio", "order-18"));
        Assert.NotEqual(one, IbkrOfflineOrderLifecycle.CreateDeterministicCoid("other", "order-17"));
        Assert.NotEqual(
            IbkrOfflineOrderLifecycle.CreateDeterministicCoid("ab", "c"),
            IbkrOfflineOrderLifecycle.CreateDeterministicCoid("a", "bc"));
    }

    [Fact]
    public void DuplicateBusinessIntent_ReturnsSameState_ButCollisionFails()
    {
        var model = New();
        var intent = Intent();
        var original = model.Register(intent);
        Advance(model, IbkrOfflineOrderEventType.PreflightApproved);
        Assert.Equal(IbkrOfflineOrderState.PreflightApproved, model.Register(intent).State);
        Assert.Equal(IbkrOfflineOrderState.Prepared, original.State);

        Assert.Throws<InvalidOperationException>(() => model.Register(intent with { Quantity = 2m }));
        Assert.Throws<ArgumentOutOfRangeException>(() => model.Register(intent with { ClientOrderId = "other", Quantity = 0m }));
    }

    [Fact]
    public void CannotStartSubmissionWithoutPreflight_OrAfterPreflightRejection()
    {
        var model = New();
        Assert.Throws<InvalidOperationException>(() => Advance(model, IbkrOfflineOrderEventType.SubmissionStarted));
        Advance(model, IbkrOfflineOrderEventType.PreflightRejected);
        Assert.Equal(IbkrOfflineOrderState.Rejected, model.Get(Coid).State);
        Assert.Throws<InvalidOperationException>(() => Advance(model, IbkrOfflineOrderEventType.PreflightApproved));
        Assert.Throws<InvalidOperationException>(() => Advance(model, IbkrOfflineOrderEventType.SubmissionStarted));
    }

    [Fact]
    public void ReplyChain_RequiresExplicitConfirmationAndAcknowledgement()
    {
        var model = Submitted();
        Assert.Equal(IbkrOfflineOrderState.AwaitingReply,
            Advance(model, IbkrOfflineOrderEventType.ReplyRequired, replyId: "r1").State);
        Assert.Equal(IbkrOfflineOrderState.AwaitingReply,
            Advance(model, IbkrOfflineOrderEventType.ReplyRequired, replyId: "r1").State);
        Assert.Throws<InvalidOperationException>(() =>
            Advance(model, IbkrOfflineOrderEventType.BrokerAcknowledged, brokerId: BrokerId));

        Assert.Equal(IbkrOfflineOrderState.AwaitingAcknowledgement,
            Advance(model, IbkrOfflineOrderEventType.ReplyConfirmed, replyId: "r1").State);
        Assert.Equal(IbkrOfflineOrderState.AwaitingAcknowledgement,
            Advance(model, IbkrOfflineOrderEventType.ReplyConfirmed, replyId: "r1").State);
        Advance(model, IbkrOfflineOrderEventType.ReplyRequired, replyId: "r2");
        Advance(model, IbkrOfflineOrderEventType.ReplyConfirmed, replyId: "r2");
        var accepted = Advance(model, IbkrOfflineOrderEventType.BrokerAcknowledged, brokerId: BrokerId);
        Assert.Equal(IbkrOfflineOrderState.Working, accepted.State);
        Assert.Equal(BrokerId, accepted.BrokerOrderId);
        Assert.Equal(2, accepted.ReplySteps);
        Assert.Throws<InvalidOperationException>(() => Advance(model, IbkrOfflineOrderEventType.SubmissionStarted));
    }

    [Fact]
    public void ReplyChain_RejectsCyclesAndExcessiveSteps()
    {
        var model = Submitted();
        for (var i = 0; i < 3; i++)
        {
            Advance(model, IbkrOfflineOrderEventType.ReplyRequired, replyId: $"reply-{i}");
            Advance(model, IbkrOfflineOrderEventType.ReplyConfirmed, replyId: $"reply-{i}");
        }

        Assert.Throws<InvalidOperationException>(() =>
            Advance(model, IbkrOfflineOrderEventType.ReplyRequired, replyId: "reply-0"));
        Assert.Throws<InvalidOperationException>(() =>
            Advance(model, IbkrOfflineOrderEventType.ReplyRequired, replyId: "reply-3"));
        Assert.Equal(3, model.Get(Coid).ReplySteps);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void FailedSubmission_BecomesUnknownAndNeverBlindlyResubmits(int httpStatus)
    {
        var model = Submitted();
        var unknown = Advance(model, IbkrOfflineOrderEventType.HttpFailure, httpStatus: httpStatus);

        Assert.Equal(IbkrOfflineOrderState.Unknown, unknown.State);
        Assert.Equal(httpStatus, unknown.LastHttpStatus);
        Assert.Throws<InvalidOperationException>(() =>
            Advance(model, IbkrOfflineOrderEventType.SubmissionStarted));
        Assert.Throws<InvalidOperationException>(() =>
            Advance(model, IbkrOfflineOrderEventType.CancelRequested));

        Assert.Equal(IbkrOfflineOrderState.Unknown,
            Advance(model, IbkrOfflineOrderEventType.ReconciliationNotFound).State);
        Assert.Equal(IbkrOfflineOrderState.Working,
            Advance(model, IbkrOfflineOrderEventType.BrokerAcknowledged, brokerId: BrokerId).State);
    }

    [Fact]
    public void UnexpectedHttpStatusOrOutOfPhaseFailure_DoesNotCorruptSnapshot()
    {
        var model = Submitted();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Advance(model, IbkrOfflineOrderEventType.HttpFailure, httpStatus: 418));
        Assert.Equal(IbkrOfflineOrderState.AwaitingAcknowledgement, model.Get(Coid).State);
        Advance(model, IbkrOfflineOrderEventType.BrokerAcknowledged, brokerId: BrokerId);
        Assert.Throws<InvalidOperationException>(() =>
            Advance(model, IbkrOfflineOrderEventType.HttpFailure, httpStatus: 500));
        Assert.Equal(IbkrOfflineOrderState.Working, model.Get(Coid).State);
    }

    [Fact]
    public void PartiallyFilled_MonotonicCumulativeReplaysWithoutDoubleCounting()
    {
        var model = Working(quantity: 7m);
        Assert.Equal(2m, Advance(model, IbkrOfflineOrderEventType.CumulativeFill, filled: 2m).CumulativeFilled);
        Assert.Equal(2m, Advance(model, IbkrOfflineOrderEventType.CumulativeFill, filled: 2m).CumulativeFilled);
        Assert.Equal(IbkrOfflineOrderState.PartiallyFilled, model.Get(Coid).State);

        Assert.Throws<InvalidOperationException>(() =>
            Advance(model, IbkrOfflineOrderEventType.CumulativeFill, filled: 1m));
        Assert.Throws<InvalidOperationException>(() =>
            Advance(model, IbkrOfflineOrderEventType.CumulativeFill, filled: 8m));
        Assert.Equal(2m, model.Get(Coid).CumulativeFilled);

        Assert.Equal(IbkrOfflineOrderState.Filled,
            Advance(model, IbkrOfflineOrderEventType.CumulativeFill, filled: 7m).State);
        Assert.Equal(IbkrOfflineOrderState.Filled,
            Advance(model, IbkrOfflineOrderEventType.CumulativeFill, filled: 7m).State);
        Assert.Equal(IbkrOfflineOrderState.Filled,
            Advance(model, IbkrOfflineOrderEventType.CancelRequested).State);
    }

    [Fact]
    public void CancelRequest_IsIdempotent_AndPartialFillMayRaceCancel()
    {
        var model = Working(quantity: 5m);
        Advance(model, IbkrOfflineOrderEventType.CumulativeFill, filled: 2m);
        Assert.Equal(IbkrOfflineOrderState.CancelPending,
            Advance(model, IbkrOfflineOrderEventType.CancelRequested).State);
        Assert.Equal(IbkrOfflineOrderState.CancelPending,
            Advance(model, IbkrOfflineOrderEventType.CancelRequested).State);
        Assert.Equal(IbkrOfflineOrderState.CancelPending,
            Advance(model, IbkrOfflineOrderEventType.CumulativeFill, filled: 3m).State);
        Assert.Equal(IbkrOfflineOrderState.Cancelled,
            Advance(model, IbkrOfflineOrderEventType.CancelConfirmed).State);
        Assert.Equal(IbkrOfflineOrderState.Cancelled,
            Advance(model, IbkrOfflineOrderEventType.CancelConfirmed).State);
        Assert.Equal(4m,
            Advance(model, IbkrOfflineOrderEventType.CumulativeFill, filled: 4m).CumulativeFilled);
        Assert.Equal(IbkrOfflineOrderState.Cancelled, model.Get(Coid).State);
        Assert.Equal(IbkrOfflineOrderState.Filled,
            Advance(model, IbkrOfflineOrderEventType.CumulativeFill, filled: 5m).State);
    }

    [Fact]
    public void FullFillBeforeCancelAck_CannotRegressToCancelled()
    {
        var model = Working(quantity: 3m);
        Advance(model, IbkrOfflineOrderEventType.CancelRequested);
        Advance(model, IbkrOfflineOrderEventType.CumulativeFill, filled: 3m);
        Assert.Equal(IbkrOfflineOrderState.Filled,
            Advance(model, IbkrOfflineOrderEventType.CancelConfirmed).State);
    }

    [Fact]
    public void Reconnection_CannotSubmitAgainBeforeBrokerReconciliation()
    {
        var model = Working(quantity: 5m);
        Advance(model, IbkrOfflineOrderEventType.CumulativeFill, filled: 2m);
        Assert.Equal(IbkrOfflineOrderState.Unknown,
            Advance(model, IbkrOfflineOrderEventType.Disconnected).State);
        Assert.Throws<InvalidOperationException>(() =>
            Advance(model, IbkrOfflineOrderEventType.SubmissionStarted));
        Assert.Throws<InvalidOperationException>(() =>
            Advance(model, IbkrOfflineOrderEventType.CumulativeFill, filled: 3m));
        Assert.Throws<InvalidOperationException>(() =>
            Advance(model, IbkrOfflineOrderEventType.BrokerAcknowledged, brokerId: BrokerId));
        Assert.Throws<InvalidOperationException>(() =>
            Advance(model, IbkrOfflineOrderEventType.ReconciledWorking, brokerId: "wrong", filled: 3m));
        Assert.Throws<InvalidOperationException>(() =>
            Advance(model, IbkrOfflineOrderEventType.ReconciledWorking, brokerId: BrokerId, filled: 1m));
        var restored = Advance(model, IbkrOfflineOrderEventType.ReconciledWorking, brokerId: BrokerId, filled: 3m);
        Assert.Equal(IbkrOfflineOrderState.PartiallyFilled, restored.State);
        Assert.Equal(3m, restored.CumulativeFilled);
    }

    [Fact]
    public void CancelTimeout_MustReconcileBeforeRequestingAnythingElse()
    {
        var model = Working(quantity: 4m);
        Advance(model, IbkrOfflineOrderEventType.CancelRequested);
        Advance(model, IbkrOfflineOrderEventType.HttpFailure, httpStatus: 503);
        Assert.Equal(IbkrOfflineOrderState.Unknown, model.Get(Coid).State);
        Assert.Throws<InvalidOperationException>(() =>
            Advance(model, IbkrOfflineOrderEventType.CancelRequested));
        Assert.Throws<InvalidOperationException>(() =>
            Advance(model, IbkrOfflineOrderEventType.BrokerAcknowledged, brokerId: BrokerId));
        Assert.Equal(IbkrOfflineOrderState.Cancelled,
            Advance(model, IbkrOfflineOrderEventType.ReconciledCancelled, brokerId: BrokerId, filled: 1m).State);
        Assert.Equal(1m, model.Get(Coid).CumulativeFilled);
    }

    [Fact]
    public void Reconciliation_RequiresVerifiedOrderIdAndConsistentQuantity()
    {
        var model = Submitted();
        Advance(model, IbkrOfflineOrderEventType.Disconnected);
        Assert.Throws<ArgumentException>(() =>
            Advance(model, IbkrOfflineOrderEventType.ReconciledFilled, brokerId: " ", filled: 1m));
        Assert.Throws<InvalidOperationException>(() =>
            Advance(model, IbkrOfflineOrderEventType.ReconciledFilled, brokerId: BrokerId, filled: 0m));
        Assert.Equal(IbkrOfflineOrderState.Filled,
            Advance(model, IbkrOfflineOrderEventType.ReconciledFilled, brokerId: BrokerId, filled: 1m).State);
        Assert.Throws<InvalidOperationException>(() =>
            Advance(model, IbkrOfflineOrderEventType.ReconciledWorking, brokerId: BrokerId, filled: 1m));
    }

    [Fact]
    public void BrokerRejection_CannotEraseAnAcknowledgedExecution()
    {
        var model = Working();
        Assert.Throws<InvalidOperationException>(() => Advance(model, IbkrOfflineOrderEventType.BrokerRejected));
        Assert.Equal(IbkrOfflineOrderState.Working, model.Get(Coid).State);
    }

    [Fact]
    public void Replay_IsDeterministicAndEntirelyInMemory()
    {
        static IbkrOfflineOrderSnapshot Run()
        {
            var model = New();
            var history = model.Replay(new[]
            {
                new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.PreflightApproved),
                new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.SubmissionStarted),
                new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.ReplyRequired, ReplyId: "r1"),
                new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.ReplyConfirmed, ReplyId: "r1"),
                new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.BrokerAcknowledged, BrokerOrderId: BrokerId),
                new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.CumulativeFill, CumulativeFilled: 1m)
            });
            Assert.Equal(6, history.Count);
            return model.Get(Coid);
        }

        Assert.Equal(Run(), Run());
        Assert.Equal(IbkrOfflineOrderState.Filled, Run().State);
    }

    private static IbkrOfflineOrderIntent Intent(decimal quantity = 1m) =>
        new(Coid, "offline-paper-fixture", "AAPL", OrderSide.Buy, quantity);

    private static IbkrOfflineOrderLifecycle New(decimal quantity = 1m)
    {
        var model = new IbkrOfflineOrderLifecycle();
        model.Register(Intent(quantity));
        return model;
    }

    private static IbkrOfflineOrderLifecycle Submitted(decimal quantity = 1m)
    {
        var model = New(quantity);
        Advance(model, IbkrOfflineOrderEventType.PreflightApproved);
        Advance(model, IbkrOfflineOrderEventType.SubmissionStarted);
        return model;
    }

    private static IbkrOfflineOrderLifecycle Working(decimal quantity = 1m)
    {
        var model = Submitted(quantity);
        Advance(model, IbkrOfflineOrderEventType.BrokerAcknowledged, brokerId: BrokerId);
        return model;
    }

    private static IbkrOfflineOrderSnapshot Advance(
        IbkrOfflineOrderLifecycle model,
        IbkrOfflineOrderEventType type,
        string? brokerId = null,
        string? replyId = null,
        decimal? filled = null,
        int? httpStatus = null) =>
        model.Apply(new IbkrOfflineOrderEvent(
            Coid, type, brokerId, replyId, filled, httpStatus));
}
