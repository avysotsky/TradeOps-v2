using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Ibkr;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class IbkrOfflineBrokerReconciliationSourceTests
{
    private const string Account = "synthetic-ibkr-paper";
    private const string Client = "coid-synthetic-1";
    private const string Broker = "broker-synthetic-1";
    private const string Symbol = "AAPL";
    private const string Batch = "cursor-batch";

    private static readonly IBrokerReconciliationDecisionEngine Engine =
        new BrokerReconciliationDecisionEngine(new OrderStateMachine());

    [Fact]
    public async Task CompleteTwoPageChain_ProducesUnifiedFilledDecision()
    {
        var source = new IbkrOfflineBrokerReconciliationSource(
            FullCollector(Order(
                IbkrOfflineBrokerOrderStatus.Filled,
                5m,
                [Execution("fill-a", 2m), Execution("fill-b", 3m)])));

        IBrokerReconciliationSource asPort = source;
        var evidence = await asPort.FetchOrderEvidenceAsync(Context());
        var result = await new BrokerReconciliationShadowEvaluator(source, Engine)
            .EvaluateAsync(Context());

        Assert.Equal(BrokerEvidenceAvailability.Complete, evidence.Availability);
        Assert.True(evidence.OrdersComplete);
        Assert.True(evidence.ExecutionsComplete);
        Assert.Equal(IbkrOfflineBrokerReconciliationSource.MakeSyntheticInstrumentKey(Symbol),
            evidence.Order?.InstrumentKey);
        Assert.Equal(OrderStatus.Filled, evidence.Order?.Status);
        Assert.Equal(19L, evidence.ObservationSequence);
        Assert.Equal(BrokerReconciliationDecisionKind.Candidate, result.Decision.Kind);
        Assert.Equal(5m, result.Decision.CandidateFilledQuantity);
    }

    [Fact]
    public async Task MissingTerminalCursorPage_RemainsIncompleteEvenIfCollectorLaterCompletes()
    {
        var collector = NewCollector();
        collector.Append(Page(0, null, "cursor-next", false,
            Order(IbkrOfflineBrokerOrderStatus.Filled, 5m,
                [Execution("fill-a", 5m)])));

        var incompleteSource = new IbkrOfflineBrokerReconciliationSource(collector);
        var first = await new BrokerReconciliationShadowEvaluator(
            incompleteSource, Engine).EvaluateAsync(Context());

        Assert.Equal(BrokerEvidenceAvailability.Incomplete, first.Availability);
        Assert.Equal(BrokerReconciliationDecisionKind.Unresolved, first.Decision.Kind);
        Assert.Null(first.Decision.CandidateStatus);

        collector.Append(Page(1, "cursor-next", null, true));

        // Incomplete source is a FROZEN past observation. Its semantics
        // must not mutate when the caller appends a later page.
        var stale = await incompleteSource.FetchOrderEvidenceAsync(Context());
        Assert.Equal(BrokerEvidenceAvailability.Incomplete, stale.Availability);

        var completeSource = new IbkrOfflineBrokerReconciliationSource(collector);
        var complete = await new BrokerReconciliationShadowEvaluator(
            completeSource, Engine).EvaluateAsync(Context());
        Assert.Equal(BrokerEvidenceAvailability.Complete, complete.Availability);
        Assert.Equal(BrokerReconciliationDecisionKind.Candidate, complete.Decision.Kind);
    }

    [Fact]
    public async Task EmptyCompleteCollection_IsNotFoundNotResubmitPermission()
    {
        var source = new IbkrOfflineBrokerReconciliationSource(FullCollector());

        var result = await new BrokerReconciliationShadowEvaluator(source, Engine)
            .EvaluateAsync(Context());

        Assert.Equal(BrokerEvidenceAvailability.NotFound, result.Availability);
        Assert.Equal(BrokerReconciliationDecisionKind.Unresolved, result.Decision.Kind);
        Assert.Null(result.Decision.CandidateStatus);
    }

    [Fact]
    public async Task MissingOrderInPartialChain_CannotProveAbsence()
    {
        var collector = NewCollector();
        collector.Append(Page(0, null, "missing-last-page", false));

        var source = new IbkrOfflineBrokerReconciliationSource(collector);
        var result = await new BrokerReconciliationShadowEvaluator(source, Engine)
            .EvaluateAsync(Context());

        Assert.Equal(BrokerEvidenceAvailability.Incomplete, result.Availability);
        Assert.Equal(BrokerReconciliationDecisionKind.Unresolved, result.Decision.Kind);
    }

    [Fact]
    public async Task UnfinishedExecutionLedger_RemainsIncompleteWithCompleteOrderPages()
    {
        var source = new IbkrOfflineBrokerReconciliationSource(FullCollector(
            Order(IbkrOfflineBrokerOrderStatus.PartiallyFilled, 3m,
                [Execution("fill-a", 2m)], executionsComplete: false)));

        var result = await new BrokerReconciliationShadowEvaluator(source, Engine)
            .EvaluateAsync(Context());

        Assert.Equal(BrokerEvidenceAvailability.Incomplete, result.Availability);
        Assert.True((await source.FetchOrderEvidenceAsync(Context())).OrdersComplete);
        Assert.Equal(BrokerReconciliationDecisionKind.Unresolved, result.Decision.Kind);
    }

    [Theory]
    [InlineData(IbkrOfflineBrokerOrderStatus.PendingSubmit, OrderStatus.Submitted)]
    [InlineData(IbkrOfflineBrokerOrderStatus.PreSubmitted, OrderStatus.Submitted)]
    [InlineData(IbkrOfflineBrokerOrderStatus.Submitted, OrderStatus.Accepted)]
    public async Task UnfilledWorkingBrokerStatuses_MapToNormalizedWorking(
        IbkrOfflineBrokerOrderStatus status, OrderStatus expected)
    {
        var source = new IbkrOfflineBrokerReconciliationSource(FullCollector(
            Order(status, 0m, [])));

        var result = await new BrokerReconciliationShadowEvaluator(source, Engine)
            .EvaluateAsync(Context());

        Assert.Equal(BrokerEvidenceAvailability.Complete, result.Availability);
        Assert.Equal(BrokerReconciliationDecisionKind.Candidate, result.Decision.Kind);
        Assert.Equal(expected, result.Decision.CandidateStatus);
        Assert.Equal(0m, result.Decision.CandidateFilledQuantity);
    }

    [Theory]
    [InlineData(IbkrOfflineBrokerOrderStatus.Unknown)]
    [InlineData(IbkrOfflineBrokerOrderStatus.PendingCancel)]
    [InlineData(IbkrOfflineBrokerOrderStatus.Rejected)]
    public async Task NonResolvedBrokerStatus_CannotBecomeAutomaticMutation(
        IbkrOfflineBrokerOrderStatus status)
    {
        var source = new IbkrOfflineBrokerReconciliationSource(FullCollector(
            Order(status, 0m, [])));

        var result = await new BrokerReconciliationShadowEvaluator(source, Engine)
            .EvaluateAsync(Context());

        Assert.Equal(BrokerReconciliationDecisionKind.Unresolved, result.Decision.Kind);
        Assert.Null(result.Decision.CandidateStatus);
    }

    [Fact]
    public async Task PartiallyFilledAndCancelledWithRecordedExecutions_AreCandidates()
    {
        foreach (var status in new[]
        {
            IbkrOfflineBrokerOrderStatus.PartiallyFilled,
            IbkrOfflineBrokerOrderStatus.Cancelled
        })
        {
            var source = new IbkrOfflineBrokerReconciliationSource(FullCollector(
                Order(status, 2m, [Execution("fill-a", 2m)])));

            var result = await new BrokerReconciliationShadowEvaluator(source, Engine)
                .EvaluateAsync(Context());

            Assert.Equal(BrokerReconciliationDecisionKind.Candidate, result.Decision.Kind);
            Assert.Equal(2m, result.Decision.CandidateFilledQuantity);
            Assert.Equal(
                status == IbkrOfflineBrokerOrderStatus.Cancelled
                    ? OrderStatus.Cancelled : OrderStatus.PartiallyFilled,
                result.Decision.CandidateStatus);
        }
    }

    [Fact]
    public async Task WrongProviderAccountOrSyntheticInstrument_RejectsInsteadOfNotFound()
    {
        var source = new IbkrOfflineBrokerReconciliationSource(FullCollector(
            Order(IbkrOfflineBrokerOrderStatus.Filled, 5m, [Execution("fill-a", 5m)])));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.FetchOrderEvidenceAsync(Context() with
            {
                ProviderKey = "Mock"
            }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.FetchOrderEvidenceAsync(Context() with
            {
                AccountKey = "other-account"
            }));

        var actual = await source.FetchOrderEvidenceAsync(Context());
        Assert.Throws<InvalidOperationException>(() => Engine.Evaluate(
            Context() with { InstrumentKey = "AAPL" }, actual));
        Assert.Throws<InvalidOperationException>(() => Engine.Evaluate(
            Context() with { InstrumentKey = "REAL-IBKR-CONID:265598" }, actual));
        Assert.Throws<InvalidOperationException>(() => Engine.Evaluate(
            Context() with { BrokerOrderId = "wrong-broker" }, actual));
    }

    [Fact]
    public async Task KnownBrokerOrderIdOnDifferentCoid_CannotBeMissingOrder()
    {
        var source = new IbkrOfflineBrokerReconciliationSource(FullCollector(
            Order(IbkrOfflineBrokerOrderStatus.Submitted, 0m, [])));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.FetchOrderEvidenceAsync(Context() with
            {
                ClientOrderId = "different-client",
                BrokerOrderId = Broker
            }));
    }

    [Fact]
    public void IncompleteEmptyCollectorAndInvalidSyntheticKeys_FailClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new IbkrOfflineBrokerReconciliationSource(NewCollector()));
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineBrokerReconciliationSource.MakeSyntheticInstrumentKey(""));
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineBrokerReconciliationSource.MakeSyntheticInstrumentKey(
                "AAPL\nWRONG"));
    }

    [Fact]
    public async Task CancellationPropagatesNoEvidenceOrMutation()
    {
        var source = new IbkrOfflineBrokerReconciliationSource(FullCollector());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            source.FetchOrderEvidenceAsync(Context(), cancelled.Token));
    }

    private static BrokerOrderReconciliationContext Context() =>
        new(IbkrOfflineBrokerReconciliationSource.ProviderKey,
            Account, Client, null,
            IbkrOfflineBrokerReconciliationSource.MakeSyntheticInstrumentKey(Symbol),
            OrderSide.Buy, 5m, OrderStatus.Unknown, 0m, 0L);

    private static IbkrOfflineEvidencePageCollector NewCollector() =>
        new(Account, Batch, observationSequence: 19);

    private static IbkrOfflineEvidencePageCollector FullCollector(
        params IbkrOfflineBrokerOrderEvidence[] orders)
    {
        var collector = NewCollector();
        collector.Append(Page(0, null, "page-2", false));
        collector.Append(Page(1, "page-2", null, true, orders));
        return collector;
    }

    private static IbkrOfflineEvidencePage Page(
        int index, string? request, string? next, bool last,
        params IbkrOfflineBrokerOrderEvidence[] orders) =>
        new(Account, Batch, ObservationSequence: 19,
            PageIndex: index, RequestCursor: request, NextCursor: next,
            IsLastPage: last, Orders: orders);

    private static IbkrOfflineExecutionEvidence Execution(
        string executionId, decimal quantity) =>
        new(executionId, Account, Client, Broker, quantity);

    private static IbkrOfflineBrokerOrderEvidence Order(
        IbkrOfflineBrokerOrderStatus status,
        decimal filled,
        IReadOnlyList<IbkrOfflineExecutionEvidence> executions,
        bool executionsComplete = true) =>
        new(Account, Client, Broker, Symbol,
            OrderSide.Buy, 5m, filled, status,
            ExecutionsComplete: executionsComplete,
            Executions: executions);
}
