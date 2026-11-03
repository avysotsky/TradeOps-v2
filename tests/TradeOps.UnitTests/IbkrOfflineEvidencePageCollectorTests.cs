using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Ibkr;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class IbkrOfflineEvidencePageCollectorTests
{
    private const string Account = "synthetic-paper";
    private const string Batch = "offline-batch-001";
    private const string Client = "to-synthetic-order";
    private const string Broker = "offline-broker-order-11";

    [Fact]
    public void TwoConsecutivePages_GenerateCompleteEvidence_AndResolveUnknown()
    {
        var collector = NewCollector();
        collector.Append(Page(0, null, "cursor-1", false));
        Assert.False(collector.IsComplete);
        collector.Append(Page(1, "cursor-1", null, true, Order()));

        Assert.True(collector.IsComplete);
        Assert.Equal(2, collector.AcceptedPageCount);
        var full = collector.RequireCompleteSnapshot();
        Assert.True(full.OrdersComplete);
        Assert.Single(full.Orders);
        Assert.Equal(17, full.Sequence);

        var lifecycle = UncertainOrder();
        var reconciled = new IbkrOfflineSnapshotReconciler(lifecycle)
            .Reconcile(Client, full);
        Assert.Equal(IbkrOfflineReconciliationDisposition.Working, reconciled.Disposition);
        Assert.Equal(IbkrOfflineOrderState.Working, lifecycle.Get(Client).State);
    }

    [Fact]
    public void MissingTerminalPage_AlwaysRemainsInconclusive()
    {
        var collector = NewCollector();
        collector.Append(Page(0, null, "cursor-1", false, Order()));
        Assert.Throws<InvalidOperationException>(
            () => collector.RequireCompleteSnapshot());

        var partial = collector.GetPartialSnapshot();
        Assert.False(partial.OrdersComplete);
        var lifecycle = UncertainOrder();
        var result = new IbkrOfflineSnapshotReconciler(lifecycle)
            .Reconcile(Client, partial);
        Assert.Equal(IbkrOfflineReconciliationDisposition.Inconclusive, result.Disposition);
        Assert.Equal(IbkrOfflineOrderState.Unknown, lifecycle.Get(Client).State);
    }

    [Fact]
    public void EmptyTerminalPage_IsCompleteButAbsenceNeverAuthorizesResubmission()
    {
        var collector = NewCollector();
        collector.Append(Page(0, null, null, true));

        var lifecycle = UncertainOrder();
        var result = new IbkrOfflineSnapshotReconciler(lifecycle)
            .Reconcile(Client, collector.RequireCompleteSnapshot());
        Assert.Equal(IbkrOfflineReconciliationDisposition.NotFound, result.Disposition);
        Assert.Equal(IbkrOfflineOrderState.Unknown, lifecycle.Get(Client).State);
        Assert.Throws<InvalidOperationException>(() =>
            lifecycle.Apply(new IbkrOfflineOrderEvent(
                Client, IbkrOfflineOrderEventType.SubmissionStarted)));
    }

    [Fact]
    public void ExactPageReplay_IsIdempotentEvenAfterCompletion()
    {
        var collector = NewCollector();
        var first = Page(0, null, "p2", false, Order());
        var last = Page(1, "p2", null, true);
        collector.Append(first);
        collector.Append(first with { Orders = [Order()] });
        collector.Append(last);
        collector.Append(first);
        collector.Append(last);

        Assert.True(collector.IsComplete);
        Assert.Equal(2, collector.AcceptedPageCount);
        Assert.Single(collector.RequireCompleteSnapshot().Orders);
    }

    [Fact]
    public void ConflictingPageReplay_RejectedWithoutConsumingNewCursor()
    {
        var collector = NewCollector();
        collector.Append(Page(0, null, "cursor-1", false, Order()));

        Assert.Throws<InvalidOperationException>(() =>
            collector.Append(Page(0, null, "different", false, Order())));
        Assert.Equal(1, collector.AcceptedPageCount);
        collector.Append(Page(1, "cursor-1", null, true));
        Assert.True(collector.IsComplete);
    }

    [Theory]
    [InlineData(1, null, null)]
    [InlineData(0, "unexpected", "cursor-1")]
    [InlineData(0, null, null)]
    [InlineData(-1, null, "cursor-1")]
    public void InvalidFirstPage_CannotBeAccepted(
        int index, string? request, string? next)
    {
        var collector = NewCollector();
        Assert.Throws<InvalidOperationException>(() =>
            collector.Append(Page(index, request, next, false)));
        Assert.Equal(0, collector.AcceptedPageCount);
    }

    [Fact]
    public void SkippedPageOrIncorrectCursor_RejectsThenAcceptsCorrectContinuation()
    {
        var collector = NewCollector();
        collector.Append(Page(0, null, "cursor-A", false));

        Assert.Throws<InvalidOperationException>(() =>
            collector.Append(Page(2, "cursor-A", null, true)));
        Assert.Throws<InvalidOperationException>(() =>
            collector.Append(Page(1, "cursor-B", null, true)));

        Assert.Equal(1, collector.AcceptedPageCount);
        collector.Append(Page(1, "cursor-A", null, true, Order()));
        Assert.True(collector.IsComplete);
    }

    [Fact]
    public void CursorCyclesAndMalformedTokens_FailClosed()
    {
        var collector = NewCollector();
        collector.Append(Page(0, null, "cursor-1", false));
        Assert.Throws<InvalidOperationException>(() =>
            collector.Append(Page(1, "cursor-1", "cursor-1", false)));
        Assert.Throws<InvalidOperationException>(() =>
            collector.Append(Page(1, "cursor-1", "cursor-2/unsafe", false)));
        Assert.Throws<InvalidOperationException>(() =>
            collector.Append(Page(1, "cursor-1", "cursor-2\n", false)));
        collector.Append(Page(1, "cursor-1", "cursor-2", false));
        Assert.Throws<InvalidOperationException>(() =>
            collector.Append(Page(2, "cursor-2", "cursor-1", false)));
        collector.Append(Page(2, "cursor-2", null, true));
        Assert.True(collector.IsComplete);
    }

    [Fact]
    public void TerminalPageCannotAdvertiseFurtherCursorOrReceiveMorePages()
    {
        var collector = NewCollector();
        Assert.Throws<InvalidOperationException>(() =>
            collector.Append(Page(0, null, "cursor-next", true)));
        collector.Append(Page(0, null, null, true));
        Assert.Throws<InvalidOperationException>(() =>
            collector.Append(Page(1, "cursor-next", null, true)));
    }

    [Fact]
    public void CrossAccountCollectionOrObservationSequence_FailWithoutMutation()
    {
        var collector = NewCollector();
        var basePage = Page(0, null, null, true, Order());
        foreach (var bad in new[]
        {
            basePage with { AccountKey = "other-account" },
            basePage with { CollectionKey = "other-collection" },
            basePage with { ObservationSequence = 16 },
            basePage with { ObservationSequence = 18 },
            basePage with { Orders = [Order() with { AccountKey = "other-account" }] }
        })
        {
            Assert.Throws<InvalidOperationException>(() => collector.Append(bad));
            Assert.Equal(0, collector.AcceptedPageCount);
        }

        collector.Append(basePage);
        Assert.True(collector.IsComplete);
    }

    [Fact]
    public void DuplicateOrderCoidOrBrokerIdAcrossPages_FailsBeforeCommit()
    {
        var collector = NewCollector();
        collector.Append(Page(0, null, "next", false, Order()));

        Assert.Throws<InvalidOperationException>(() =>
            collector.Append(Page(1, "next", null, true, Order())));
        Assert.Throws<InvalidOperationException>(() =>
            collector.Append(Page(1, "next", null, true,
                Order() with { ClientOrderId = "other-client" })));
        Assert.Equal(1, collector.AcceptedPageCount);
        collector.Append(Page(1, "next", null, true,
            Order() with
            {
                ClientOrderId = "other-client",
                BrokerOrderId = "other-broker",
                Symbol = "MSFT"
            }));
        Assert.Equal(2, collector.RequireCompleteSnapshot().Orders.Count);
    }

    [Fact]
    public void DuplicateOrdersWithinOnePage_DoNotPartiallyAdvanceCursor()
    {
        var collector = NewCollector();
        Assert.Throws<InvalidOperationException>(() =>
            collector.Append(Page(0, null, null, true, Order(), Order())));
        Assert.Equal(0, collector.AcceptedPageCount);
        collector.Append(Page(0, null, null, true, Order()));
    }

    [Fact]
    public void BadIdentifiersStatesAndQuantityPrecision_FailClosed()
    {
        var collector = NewCollector();
        foreach (var order in new[]
        {
            Order() with { Symbol = " " },
            Order() with { ClientOrderId = "a\nb" },
            Order() with { BrokerOrderId = new string('x', 129) },
            Order() with { RequestedQuantity = -1m },
            Order() with { RequestedQuantity = 0m },
            Order() with { CumulativeFilled = -0.1m },
            Order() with { CumulativeFilled = 4m },
            Order() with { RequestedQuantity = 0.0000000000001m },
            Order() with { Status = (IbkrOfflineBrokerOrderStatus)999 },
            Order() with { Side = (OrderSide)999 }
        })
        {
            Assert.Throws<InvalidOperationException>(() =>
                collector.Append(Page(0, null, null, true, order)));
            Assert.Equal(0, collector.AcceptedPageCount);
        }

        collector.Append(Page(0, null, null, true, Order()));
    }

    [Fact]
    public void InvalidExecutionIdentityQuantityOrDuplicateId_CannotBeLaundered()
    {
        var collector = NewCollector();
        var first = Execution("execution-1", 1m);
        var bad = new[]
        {
            first with { AccountKey = "other-account" },
            first with { ClientOrderId = "other-client" },
            first with { BrokerOrderId = "other-broker" },
            first with { Quantity = 0m },
            first with { Quantity = -1m },
            first with { ExecutionId = " " },
            first with { Quantity = 0.0000000000001m }
        };
        foreach (var execution in bad)
        {
            var invalid = Order() with
            {
                CumulativeFilled = 1m,
                Executions = [execution]
            };
            Assert.Throws<InvalidOperationException>(() =>
                collector.Append(Page(0, null, null, true, invalid)));
        }

        Assert.Throws<InvalidOperationException>(() =>
            collector.Append(Page(0, null, null, true, Order() with
            {
                CumulativeFilled = 2m,
                Executions = [first, first]
            })));

        Assert.Throws<InvalidOperationException>(() =>
            collector.Append(Page(0, null, null, true, Order() with
            {
                CumulativeFilled = 2m,
                Executions = [first]
            })));
        Assert.Equal(0, collector.AcceptedPageCount);

        collector.Append(Page(0, null, null, true, Order() with
        {
            CumulativeFilled = 1m,
            Executions = [first]
        }));
    }

    [Fact]
    public void UnverifiedExecutionPagination_KeepsReconciliationInconclusive()
    {
        var collector = NewCollector();
        collector.Append(Page(0, null, null, true, Order() with
        {
            CumulativeFilled = 2m,
            ExecutionsComplete = false,
            Executions = [Execution("execution-1", 1m)]
        }));

        var lifecycle = UncertainOrder();
        var result = new IbkrOfflineSnapshotReconciler(lifecycle)
            .Reconcile(Client, collector.RequireCompleteSnapshot());

        Assert.Equal(IbkrOfflineReconciliationDisposition.Inconclusive, result.Disposition);
        Assert.Equal(IbkrOfflineOrderState.Unknown, lifecycle.Get(Client).State);
    }

    [Fact]
    public void AcceptedEvidenceIsDeepCopiedFromCallerOwnedArrays()
    {
        var sourceExecutions = new[] { Execution("execution-1", 1m) };
        var sourceOrders = new[] { Order() with
        {
            CumulativeFilled = 1m,
            Executions = sourceExecutions
        } };

        var collector = NewCollector();
        collector.Append(Page(0, null, null, true, sourceOrders));
        sourceExecutions[0] = Execution("tampered", 1m);
        sourceOrders[0] = Order() with { Symbol = "HACKED" };

        var evidence = collector.RequireCompleteSnapshot();
        Assert.Equal("AAPL", evidence.Orders[0].Symbol);
        Assert.Equal("execution-1", evidence.Orders[0].Executions[0].ExecutionId);
    }

    [Fact]
    public void PageOrOrderCountLimits_RejectWithoutCommittingState()
    {
        var collector = NewCollector();
        for (var i = 0; i < 31; i++)
        {
            collector.Append(Page(i, i == 0 ? null : $"c{i}", $"c{i + 1}", false));
        }

        Assert.Throws<InvalidOperationException>(() =>
            collector.Append(Page(31, "c31", "c32", false)));
        collector.Append(Page(31, "c31", null, true));
        Assert.True(collector.IsComplete);

        var tooMany = Enumerable.Range(0, 257)
            .Select(i => Order() with
            {
                ClientOrderId = $"client-{i}",
                BrokerOrderId = $"broker-{i}"
            }).ToArray();
        Assert.Throws<InvalidOperationException>(() =>
            NewCollector().Append(Page(0, null, null, true, tooMany)));
    }

    [Fact]
    public void OversizedNestedExecutionPage_RejectsBeforeAcceptingAnyPage()
    {
        var collector = NewCollector();
        var many = Enumerable.Range(0, 2050)
            .Select(i => Execution($"execution-{i}", 0.000001m))
            .ToArray();
        var first = Order() with
        {
            RequestedQuantity = 3m,
            CumulativeFilled = 0.002050m,
            Executions = many
        };
        var second = first with
        {
            ClientOrderId = "second-client",
            BrokerOrderId = "second-broker",
            Executions = many.Select(e => e with
            {
                ClientOrderId = "second-client",
                BrokerOrderId = "second-broker"
            }).ToArray()
        };

        Assert.Throws<InvalidOperationException>(() =>
            collector.Append(Page(0, null, null, true, first, second)));
        Assert.Equal(0, collector.AcceptedPageCount);
        Assert.False(collector.IsComplete);
        collector.Append(Page(0, null, null, true, Order()));
    }

    [Fact]
    public void ConstructorRequiresNonemptyScopedSequenceAndKeys()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new IbkrOfflineEvidencePageCollector("", Batch, 17));
        Assert.Throws<InvalidOperationException>(() =>
            new IbkrOfflineEvidencePageCollector(Account, "", 17));
        Assert.Throws<InvalidOperationException>(() =>
            new IbkrOfflineEvidencePageCollector(Account, Batch, 0));
        Assert.Throws<InvalidOperationException>(() =>
            new IbkrOfflineEvidencePageCollector("wrong\naccount", Batch, 17));
        Assert.Throws<InvalidOperationException>(() =>
            NewCollector().RequireCompleteSnapshot());
        Assert.Throws<InvalidOperationException>(() =>
            NewCollector().GetPartialSnapshot());
    }

    private static IbkrOfflineEvidencePageCollector NewCollector() =>
        new(Account, Batch, 17);

    private static IbkrOfflineEvidencePage Page(
        int index, string? request, string? next, bool last,
        params IbkrOfflineBrokerOrderEvidence[] orders) =>
        new(Account, Batch, 17, index, request, next, last, orders);

    private static IbkrOfflineBrokerOrderEvidence Order() =>
        new(Account, Client, Broker, "AAPL", OrderSide.Buy, 3m, 0m,
            IbkrOfflineBrokerOrderStatus.Submitted, true,
            Array.Empty<IbkrOfflineExecutionEvidence>());

    private static IbkrOfflineExecutionEvidence Execution(string id, decimal qty) =>
        new(id, Account, Client, Broker, qty);

    private static IbkrOfflineOrderLifecycle UncertainOrder()
    {
        var lifecycle = new IbkrOfflineOrderLifecycle();
        lifecycle.Register(new IbkrOfflineOrderIntent(
            Client, Account, "AAPL", OrderSide.Buy, 3m));
        lifecycle.Apply(new IbkrOfflineOrderEvent(
            Client, IbkrOfflineOrderEventType.PreflightApproved));
        lifecycle.Apply(new IbkrOfflineOrderEvent(
            Client, IbkrOfflineOrderEventType.SubmissionStarted));
        lifecycle.Apply(new IbkrOfflineOrderEvent(
            Client, IbkrOfflineOrderEventType.TransportTimedOut));
        return lifecycle;
    }
}
