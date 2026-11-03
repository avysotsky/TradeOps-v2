using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Ibkr;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class IbkrOfflineSnapshotReconcilerTests
{
    private const string Coid = "to-synthetic-order-001";
    private const string Account = "offline-paper-account";
    private const string BrokerId = "synthetic-order-42";

    [Fact]
    public void CompleteSnapshot_ResolvesUnknownToWorking_WithoutSendingAnything()
    {
        var lifecycle = UnknownBeforeAcknowledgement();
        var reconciler = new IbkrOfflineSnapshotReconciler(lifecycle);

        var result = reconciler.Reconcile(Coid, Snapshot(1, Order()));

        Assert.Equal(IbkrOfflineReconciliationDisposition.Working, result.Disposition);
        Assert.Equal(IbkrOfflineOrderState.Working, result.Order.State);
        Assert.Equal(BrokerId, result.Order.BrokerOrderId);
        Assert.Equal(0m, result.Order.CumulativeFilled);
        Assert.Throws<InvalidOperationException>(() =>
            lifecycle.Apply(new IbkrOfflineOrderEvent(
                Coid, IbkrOfflineOrderEventType.SubmissionStarted)));
    }

    [Fact]
    public void UnknownWithCompletePartialFillLedger_ResolvesExactCumulativeQuantity()
    {
        var lifecycle = UnknownBeforeAcknowledgement(quantity: 10m);
        var reconciler = new IbkrOfflineSnapshotReconciler(lifecycle);
        var evidence = Order(quantity: 10m, filled: 7m,
            status: IbkrOfflineBrokerOrderStatus.PartiallyFilled,
            executions: [Execution("exec-a", 3m), Execution("exec-b", 4m)]);

        var result = reconciler.Reconcile(Coid, Snapshot(7, evidence));

        Assert.Equal(IbkrOfflineReconciliationDisposition.PartiallyFilled, result.Disposition);
        Assert.Equal(IbkrOfflineOrderState.PartiallyFilled, result.Order.State);
        Assert.Equal(7m, result.Order.CumulativeFilled);
        Assert.Equal(10m, result.Order.Intent.Quantity);
    }

    [Fact]
    public void DuplicateExecutionInCompleteSnapshot_CountsExactlyOnce()
    {
        var lifecycle = UnknownBeforeAcknowledgement(quantity: 5m);
        var reconciler = new IbkrOfflineSnapshotReconciler(lifecycle);
        var fill = Execution("exec-1", 2m);
        var evidence = Order(quantity: 5m, filled: 2m,
            status: IbkrOfflineBrokerOrderStatus.PartiallyFilled,
            executions: [fill, fill]);

        var result = reconciler.Reconcile(Coid, Snapshot(1, evidence));
        Assert.Equal(2m, result.Order.CumulativeFilled);
        Assert.Equal(IbkrOfflineReconciliationDisposition.PartiallyFilled, result.Disposition);
    }

    [Fact]
    public void ConflictingDuplicateExecutionId_FailsClosedAndDoesNotMutateState()
    {
        var lifecycle = UnknownBeforeAcknowledgement(quantity: 5m);
        var reconciler = new IbkrOfflineSnapshotReconciler(lifecycle);
        var evidence = Order(quantity: 5m, filled: 5m,
            status: IbkrOfflineBrokerOrderStatus.Filled,
            executions: [Execution("exec-1", 2m), Execution("exec-1", 3m)]);

        Assert.Throws<InvalidOperationException>(() => reconciler.Reconcile(
            Coid, Snapshot(1, evidence)));
        Assert.Equal(IbkrOfflineOrderState.Unknown, lifecycle.Get(Coid).State);
    }

    [Fact]
    public void FullFillRequiresEntireExecutionLedgerAndMatchingQuantities()
    {
        var lifecycle = UnknownBeforeAcknowledgement(quantity: 3m);
        var reconciler = new IbkrOfflineSnapshotReconciler(lifecycle);

        var incomplete = Order(quantity: 3m, filled: 3m,
            status: IbkrOfflineBrokerOrderStatus.Filled,
            executions: [Execution("exec-1", 3m)]) with { ExecutionsComplete = false };

        Assert.Equal(IbkrOfflineReconciliationDisposition.Inconclusive,
            reconciler.Reconcile(Coid, Snapshot(1, incomplete)).Disposition);
        Assert.Equal(IbkrOfflineOrderState.Unknown, lifecycle.Get(Coid).State);

        var complete = incomplete with { ExecutionsComplete = true };
        var result = reconciler.Reconcile(Coid, Snapshot(2, complete));
        Assert.Equal(IbkrOfflineOrderState.Filled, result.Order.State);
        Assert.Equal(3m, result.Order.CumulativeFilled);
    }

    [Fact]
    public void PartialBrokerOrderPage_CannotProveWorkingOrAbsence()
    {
        var lifecycle = UnknownBeforeAcknowledgement();
        var reconciler = new IbkrOfflineSnapshotReconciler(lifecycle);

        var partial = Snapshot(1, Order()) with { OrdersComplete = false };
        Assert.Equal(IbkrOfflineReconciliationDisposition.Inconclusive,
            reconciler.Reconcile(Coid, partial).Disposition);
        Assert.Equal(IbkrOfflineOrderState.Unknown, lifecycle.Get(Coid).State);
    }

    [Fact]
    public void CompleteEmptySnapshot_LeavesUnknownAndNeverPermitsResubmission()
    {
        var lifecycle = UnknownBeforeAcknowledgement();
        var reconciler = new IbkrOfflineSnapshotReconciler(lifecycle);

        var noMatch = new IbkrOfflineEvidenceSnapshot(
            1, Account, true, Array.Empty<IbkrOfflineBrokerOrderEvidence>());
        var result = reconciler.Reconcile(Coid, noMatch);

        Assert.Equal(IbkrOfflineReconciliationDisposition.NotFound, result.Disposition);
        Assert.Equal(IbkrOfflineOrderState.Unknown, result.Order.State);
        Assert.Throws<InvalidOperationException>(() =>
            lifecycle.Apply(new IbkrOfflineOrderEvent(
                Coid, IbkrOfflineOrderEventType.SubmissionStarted)));
    }

    [Fact]
    public void MatchingCoidAndBrokerId_MustBothMatchKnownIdentity()
    {
        var lifecycle = UnknownAfterAcknowledgement();
        var reconciler = new IbkrOfflineSnapshotReconciler(lifecycle);

        Assert.Throws<InvalidOperationException>(() => reconciler.Reconcile(Coid,
            Snapshot(1, Order() with { BrokerOrderId = "wrong-broker-id" })));
        Assert.Throws<InvalidOperationException>(() => reconciler.Reconcile(Coid,
            Snapshot(1, Order() with { ClientOrderId = "other-client-order" })));

        Assert.Equal(IbkrOfflineReconciliationDisposition.Working,
            reconciler.Reconcile(Coid, Snapshot(1, Order())).Disposition);
    }

    [Fact]
    public void WrongAccountCannotMoveUnknownToAnotherAccountOrder()
    {
        var lifecycle = UnknownBeforeAcknowledgement();
        var reconciler = new IbkrOfflineSnapshotReconciler(lifecycle);

        Assert.Throws<InvalidOperationException>(() => reconciler.Reconcile(
            Coid, Snapshot(1, Order()) with { AccountKey = "other-account" }));
        Assert.Throws<InvalidOperationException>(() => reconciler.Reconcile(
            Coid, Snapshot(1, Order() with { AccountKey = "other-account" })));
        Assert.Equal(IbkrOfflineOrderState.Unknown, lifecycle.Get(Coid).State);
    }

    [Fact]
    public void WrongContractIdentityOrOrderTerms_FailClosed()
    {
        var model = UnknownBeforeAcknowledgement();
        var reconciler = new IbkrOfflineSnapshotReconciler(model);

        foreach (var invalid in new[]
        {
            Order() with { Symbol = "MSFT" },
            Order() with { Side = OrderSide.Sell },
            Order() with { RequestedQuantity = 100m },
            Order() with { RequestedQuantity = 0m },
            Order() with { CumulativeFilled = -1m },
            Order() with { CumulativeFilled = 99m },
            Order() with { Symbol = "" },
        })
        {
            Assert.Throws<InvalidOperationException>(() =>
                reconciler.Reconcile(Coid, Snapshot(1, invalid)));
            Assert.Equal(IbkrOfflineOrderState.Unknown, model.Get(Coid).State);
        }
    }

    [Fact]
    public void MultipleMatchingOrDuplicateBrokerIdentities_FailClosed()
    {
        var model = UnknownBeforeAcknowledgement();
        var reconciler = new IbkrOfflineSnapshotReconciler(model);
        var first = Order();
        var second = first with { ClientOrderId = "second-order" };

        Assert.Throws<InvalidOperationException>(() =>
            reconciler.Reconcile(Coid, Snapshot(1, first, first)));
        Assert.Throws<InvalidOperationException>(() =>
            reconciler.Reconcile(Coid, Snapshot(1, first, second)));
        Assert.Equal(IbkrOfflineOrderState.Unknown, model.Get(Coid).State);
    }

    [Fact]
    public void ExecutionAccountAndOrderBinding_MustBeVerified()
    {
        var model = UnknownBeforeAcknowledgement(quantity: 5m);
        var reconciler = new IbkrOfflineSnapshotReconciler(model);
        var good = Execution("exec-1", 2m);
        var candidates = new[]
        {
            good with { AccountKey = "wrong-account" },
            good with { ClientOrderId = "wrong-client" },
            good with { BrokerOrderId = "wrong-broker" },
            good with { Quantity = -2m },
            good with { ExecutionId = "" }
        };
        foreach (var bad in candidates)
        {
            var evidence = Order(quantity: 5m, filled: 2m,
                status: IbkrOfflineBrokerOrderStatus.PartiallyFilled,
                executions: [bad]);
            Assert.Throws<InvalidOperationException>(() =>
                reconciler.Reconcile(Coid, Snapshot(1, evidence)));
        }

        Assert.Equal(IbkrOfflineOrderState.Unknown, model.Get(Coid).State);
    }

    [Fact]
    public void InconsistentFillLedgerOrDecreasingQuantity_IsRejected()
    {
        var model = UnknownAfterPartialFill();
        var reconciler = new IbkrOfflineSnapshotReconciler(model);

        var lower = Order(quantity: 5m, filled: 1m,
            status: IbkrOfflineBrokerOrderStatus.PartiallyFilled,
            executions: [Execution("exec-a", 1m)]);
        Assert.Throws<InvalidOperationException>(() =>
            reconciler.Reconcile(Coid, Snapshot(1, lower)));

        var mismatch = Order(quantity: 5m, filled: 4m,
            status: IbkrOfflineBrokerOrderStatus.PartiallyFilled,
            executions: [Execution("exec-a", 3m)]);
        Assert.Throws<InvalidOperationException>(() =>
            reconciler.Reconcile(Coid, Snapshot(1, mismatch)));

        var valid = Order(quantity: 5m, filled: 3m,
            status: IbkrOfflineBrokerOrderStatus.PartiallyFilled,
            executions: [Execution("exec-a", 1m), Execution("exec-b", 2m)]);
        Assert.Equal(3m,
            reconciler.Reconcile(Coid, Snapshot(1, valid)).Order.CumulativeFilled);
    }

    [Fact]
    public void CancellationAfterDisconnect_RequiresVerifiedTerminalOrderSnapshot()
    {
        var model = UnknownAfterCancelRequested(quantity: 5m);
        var reconciler = new IbkrOfflineSnapshotReconciler(model);
        var cancelled = Order(quantity: 5m, filled: 2m,
            status: IbkrOfflineBrokerOrderStatus.Cancelled,
            executions: [Execution("exec-a", 2m)]);

        var result = reconciler.Reconcile(Coid, Snapshot(1, cancelled));
        Assert.Equal(IbkrOfflineReconciliationDisposition.Cancelled, result.Disposition);
        Assert.Equal(IbkrOfflineOrderState.Cancelled, result.Order.State);
        Assert.Equal(2m, result.Order.CumulativeFilled);
    }

    [Fact]
    public void FilledBeforeCancelConfirmation_MustRemainFilled()
    {
        var model = UnknownAfterCancelRequested(quantity: 5m);
        var reconciler = new IbkrOfflineSnapshotReconciler(model);
        var filled = Order(quantity: 5m, filled: 5m,
            status: IbkrOfflineBrokerOrderStatus.Filled,
            executions: [Execution("exec-a", 2m), Execution("exec-b", 3m)]);

        var result = reconciler.Reconcile(Coid, Snapshot(1, filled));
        Assert.Equal(IbkrOfflineOrderState.Filled, result.Order.State);
        Assert.Equal(5m, result.Order.CumulativeFilled);
        Assert.Equal(IbkrOfflineOrderState.Filled,
            model.Apply(new IbkrOfflineOrderEvent(
                Coid, IbkrOfflineOrderEventType.CancelConfirmed)).State);
    }

    [Theory]
    [InlineData(IbkrOfflineBrokerOrderStatus.PendingCancel)]
    [InlineData(IbkrOfflineBrokerOrderStatus.Unknown)]
    [InlineData(IbkrOfflineBrokerOrderStatus.Rejected)]
    public void NonfinalOrUnverifiedBrokerStatus_DoesNotResolveUnknown(
        IbkrOfflineBrokerOrderStatus status)
    {
        var model = UnknownAfterCancelRequested();
        var reconciler = new IbkrOfflineSnapshotReconciler(model);
        var result = reconciler.Reconcile(Coid, Snapshot(1, Order(status: status)));

        Assert.Equal(IbkrOfflineReconciliationDisposition.Inconclusive, result.Disposition);
        Assert.Equal(IbkrOfflineOrderState.Unknown, model.Get(Coid).State);
    }

    [Fact]
    public void IdenticalSnapshotReplay_IsIdempotent_ConflictingAndStaleSequencesFail()
    {
        var model = UnknownBeforeAcknowledgement();
        var reconciler = new IbkrOfflineSnapshotReconciler(model);
        var snapshot = Snapshot(3, Order());

        var first = reconciler.Reconcile(Coid, snapshot);
        var replay = reconciler.Reconcile(Coid, snapshot);
        Assert.Equal(first, replay);
        Assert.Equal(IbkrOfflineOrderState.Working, model.Get(Coid).State);

        Assert.Throws<InvalidOperationException>(() =>
            reconciler.Reconcile(Coid, Snapshot(2, Order())));
        Assert.Throws<InvalidOperationException>(() =>
            reconciler.Reconcile(Coid, Snapshot(3, Order(status: IbkrOfflineBrokerOrderStatus.PendingCancel))));
        Assert.Throws<InvalidOperationException>(() =>
            reconciler.Reconcile(Coid, Snapshot(4, Order())));
        Assert.Equal(IbkrOfflineOrderState.Working, model.Get(Coid).State);
    }

    [Fact]
    public void UnrelatedOrdersInSameAccount_DoNotCauseFalsePositive()
    {
        var model = UnknownBeforeAcknowledgement();
        var reconciler = new IbkrOfflineSnapshotReconciler(model);
        var other = Order() with
        {
            ClientOrderId = "another-order",
            BrokerOrderId = "other-broker-id",
            Symbol = "MSFT"
        };

        var result = reconciler.Reconcile(Coid, Snapshot(1, other));
        Assert.Equal(IbkrOfflineReconciliationDisposition.NotFound, result.Disposition);
        Assert.Equal(IbkrOfflineOrderState.Unknown, model.Get(Coid).State);
    }

    [Fact]
    public void InvalidOrIncompleteEvidence_CannotAdvanceUnknownOrder()
    {
        var model = UnknownBeforeAcknowledgement();
        var reconciler = new IbkrOfflineSnapshotReconciler(model);

        Assert.Throws<InvalidOperationException>(() =>
            reconciler.Reconcile(Coid, Snapshot(0, Order())));
        var partial = Snapshot(1, Order()) with { OrdersComplete = false };
        Assert.Equal(IbkrOfflineReconciliationDisposition.Inconclusive,
            reconciler.Reconcile(Coid, partial).Disposition);
        Assert.Equal(IbkrOfflineOrderState.Unknown, model.Get(Coid).State);
        Assert.Equal(IbkrOfflineReconciliationDisposition.Working,
            reconciler.Reconcile(Coid, Snapshot(2, Order())).Disposition);
    }

    private static IbkrOfflineExecutionEvidence Execution(string id, decimal quantity) =>
        new(id, Account, Coid, BrokerId, quantity);

    private static IbkrOfflineBrokerOrderEvidence Order(
        decimal quantity = 1m,
        decimal filled = 0m,
        IbkrOfflineBrokerOrderStatus status = IbkrOfflineBrokerOrderStatus.Submitted,
        IReadOnlyList<IbkrOfflineExecutionEvidence>? executions = null) =>
        new(Account, Coid, BrokerId, "AAPL", OrderSide.Buy, quantity, filled,
            status, true, executions ?? Array.Empty<IbkrOfflineExecutionEvidence>());

    private static IbkrOfflineEvidenceSnapshot Snapshot(
        long sequence, params IbkrOfflineBrokerOrderEvidence[] orders) =>
        new(sequence, Account, true, orders);

    private static IbkrOfflineOrderLifecycle UnknownBeforeAcknowledgement(decimal quantity = 1m)
    {
        var lifecycle = new IbkrOfflineOrderLifecycle();
        lifecycle.Register(new IbkrOfflineOrderIntent(
            Coid, Account, "AAPL", OrderSide.Buy, quantity));
        lifecycle.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.PreflightApproved));
        lifecycle.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.SubmissionStarted));
        lifecycle.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.TransportTimedOut));
        return lifecycle;
    }

    private static IbkrOfflineOrderLifecycle UnknownAfterAcknowledgement(decimal quantity = 1m)
    {
        var lifecycle = new IbkrOfflineOrderLifecycle();
        lifecycle.Register(new IbkrOfflineOrderIntent(
            Coid, Account, "AAPL", OrderSide.Buy, quantity));
        lifecycle.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.PreflightApproved));
        lifecycle.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.SubmissionStarted));
        lifecycle.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.BrokerAcknowledged,
            BrokerOrderId: BrokerId));
        lifecycle.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.Disconnected));
        return lifecycle;
    }

    private static IbkrOfflineOrderLifecycle UnknownAfterPartialFill()
    {
        var lifecycle = UnknownAfterAcknowledgement(quantity: 5m);
        // Reconcile first Unknown to working would be needed in production.
        // In this synthetic fixture, replay a trusted broker ACK from Unknown.
        lifecycle.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.ReconciledWorking,
            BrokerOrderId: BrokerId,
            CumulativeFilled: 0m));
        lifecycle.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.CumulativeFill,
            CumulativeFilled: 2m));
        lifecycle.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.Disconnected));
        return lifecycle;
    }

    private static IbkrOfflineOrderLifecycle UnknownAfterCancelRequested(decimal quantity = 1m)
    {
        var lifecycle = new IbkrOfflineOrderLifecycle();
        lifecycle.Register(new IbkrOfflineOrderIntent(
            Coid, Account, "AAPL", OrderSide.Buy, quantity));
        lifecycle.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.PreflightApproved));
        lifecycle.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.SubmissionStarted));
        lifecycle.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.BrokerAcknowledged,
            BrokerOrderId: BrokerId));
        lifecycle.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.CancelRequested));
        lifecycle.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.Disconnected));
        return lifecycle;
    }
}
