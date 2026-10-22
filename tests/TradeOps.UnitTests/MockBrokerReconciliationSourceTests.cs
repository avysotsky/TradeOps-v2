using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class MockBrokerReconciliationSourceTests
{
    private const string Account = "synthetic-mock-paper";
    private const string Client = "mock-coid-0001";
    private const string Broker = "mock-broker-001";
    private const string Instrument = "MOCK:BTCUSDT";
    private static readonly IBrokerReconciliationDecisionEngine DecisionEngine =
        new BrokerReconciliationDecisionEngine(new OrderStateMachine());

    [Fact]
    public async Task CompleteSyntheticLedger_ProducesNonExecutableFillCandidate()
    {
        var source = new MockBrokerReconciliationSource(
            Fixture(7, true, true, Order()));
        var shadow = new BrokerReconciliationShadowEvaluator(source, DecisionEngine);

        var result = await shadow.EvaluateAsync(Context());

        Assert.Equal(BrokerEvidenceAvailability.Complete, result.Availability);
        Assert.Equal(7L, result.ObservationSequence);
        Assert.Equal(BrokerReconciliationDecisionKind.Candidate, result.Decision.Kind);
        Assert.Equal(OrderStatus.Filled, result.Decision.CandidateStatus);
        Assert.Equal(5m, result.Decision.CandidateFilledQuantity);
    }

    [Fact]
    public async Task MissingOrder_IsNotFoundOnlyIfFullAccountListIsComplete()
    {
        var ctx = Context();
        var complete = new BrokerReconciliationShadowEvaluator(
            new MockBrokerReconciliationSource(Fixture(7, true, true)),
            DecisionEngine);
        var incomplete = new BrokerReconciliationShadowEvaluator(
            new MockBrokerReconciliationSource(Fixture(7, false, false)),
            DecisionEngine);

        var full = await complete.EvaluateAsync(ctx);
        var partial = await incomplete.EvaluateAsync(ctx);

        Assert.Equal(BrokerEvidenceAvailability.NotFound, full.Availability);
        Assert.Equal(BrokerEvidenceAvailability.Incomplete, partial.Availability);
        Assert.Equal(BrokerReconciliationDecisionKind.Unresolved, full.Decision.Kind);
        Assert.Equal(BrokerReconciliationDecisionKind.Unresolved, partial.Decision.Kind);
        Assert.Null(full.Decision.CandidateStatus);
        Assert.Null(partial.Decision.CandidateStatus);
    }

    [Fact]
    public async Task IncompleteOrderOrExecutionCoverage_CannotYieldCandidate()
    {
        foreach (var fixture in new[]
        {
            Fixture(7, false, true, Order()),
            Fixture(7, true, false, Order()),
            Fixture(7, false, false, Order())
        })
        {
            var shadow = new BrokerReconciliationShadowEvaluator(
                new MockBrokerReconciliationSource(fixture), DecisionEngine);

            var result = await shadow.EvaluateAsync(Context());

            Assert.Equal(BrokerEvidenceAvailability.Incomplete, result.Availability);
            Assert.Equal(BrokerReconciliationDecisionKind.Unresolved, result.Decision.Kind);
            Assert.Null(result.Decision.CandidateStatus);
        }
    }

    [Fact]
    public async Task FixtureDefensivelyCopiesOrderAndNestedExecutionArrays()
    {
        var originalExecutions = new[]
        {
            Fill("execution-1", 2m),
            Fill("execution-2", 3m)
        };
        var originalOrders = new[]
        {
            Order() with { Executions = originalExecutions }
        };
        var source = new MockBrokerReconciliationSource(
            Fixture(7, true, true, originalOrders));

        originalExecutions[0] = Fill("tampered", 2m);
        originalOrders[0] = Order() with { InstrumentKey = "WRONG-CONTRACT" };

        var evidence = await source.FetchOrderEvidenceAsync(Context());

        Assert.Equal(Instrument, evidence.Order?.InstrumentKey);
        Assert.Equal("execution-1", evidence.Order?.Executions[0].ExecutionId);
        Assert.Equal(BrokerReconciliationDecisionKind.Candidate,
            DecisionEngine.Evaluate(Context(), evidence).Kind);
    }

    [Fact]
    public void DuplicateCoidOrBrokerOrderIdFixture_IsRejectedBeforeReads()
    {
        var first = Order();
        Assert.Throws<InvalidOperationException>(() =>
            new MockBrokerReconciliationSource(Fixture(
                7, true, true, first, first)));
        Assert.Throws<InvalidOperationException>(() =>
            new MockBrokerReconciliationSource(Fixture(
                7, true, true, first,
                first with { ClientOrderId = "different-coid" })));
        Assert.Throws<InvalidOperationException>(() =>
            new MockBrokerReconciliationSource(Fixture(
                7, true, true, first,
                first with { BrokerOrderId = "different-broker" })));
    }

    [Fact]
    public async Task WrongVenueOrAccount_NeverBecomesNotFound()
    {
        var source = new MockBrokerReconciliationSource(Fixture(7, true, true));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.FetchOrderEvidenceAsync(
                Context() with { ProviderKey = "IbkrOffline" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.FetchOrderEvidenceAsync(
                Context() with { AccountKey = "different-account" }));
    }

    [Fact]
    public async Task KnownBrokerIdOnAnotherCoid_IsNotAValidMissingOrder()
    {
        var source = new MockBrokerReconciliationSource(
            Fixture(7, true, true, Order()));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.FetchOrderEvidenceAsync(Context() with
            {
                ClientOrderId = "different-coid",
                BrokerOrderId = Broker
            }));
    }

    [Fact]
    public async Task CancellationAndSourceFailure_AreNotConvertedToMissing()
    {
        var shadow = new BrokerReconciliationShadowEvaluator(
            new MockBrokerReconciliationSource(Fixture(7, true, true, Order())),
            DecisionEngine);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            shadow.EvaluateAsync(Context(), cts.Token));

        var failing = new BrokerReconciliationShadowEvaluator(
            new FailingSource(), DecisionEngine);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failing.EvaluateAsync(Context()));
    }

    [Fact]
    public async Task IndependentRequests_ReturnStableObservation_NoOrderMutation()
    {
        var source = new MockBrokerReconciliationSource(
            Fixture(7, true, true, Order()));
        var a = await source.FetchOrderEvidenceAsync(Context());
        var b = await source.FetchOrderEvidenceAsync(Context());

        Assert.Equal(a, b);
        Assert.Equal(BrokerReconciliationDecisionKind.Candidate,
            DecisionEngine.Evaluate(Context(), b).Kind);
    }

    [Fact]
    public void InvalidFixtureAccountSequenceOrCrossAccountRow_FailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new MockBrokerReconciliationSource(Fixture(0, true, true)));
        Assert.Throws<InvalidOperationException>(() =>
            new MockBrokerReconciliationSource(
                Fixture(7, true, true, Order() with
                {
                    AccountKey = "wrong-account"
                })));
    }

    private static MockBrokerEvidenceSnapshot Fixture(
        long sequence, bool ordersComplete, bool executionsComplete,
        params NormalizedBrokerOrderEvidence[] orders) =>
        new(Account, sequence, ordersComplete, executionsComplete, orders);

    private static BrokerOrderReconciliationContext Context() =>
        new(MockBrokerReconciliationSource.ProviderKey, Account, Client,
            null, Instrument, OrderSide.Buy, 5m, OrderStatus.Unknown, 0m, 0L);

    private static NormalizedBrokerExecutionEvidence Fill(
        string id, decimal quantity) => new(id, Account, Client, Broker, quantity);

    private static NormalizedBrokerOrderEvidence Order() =>
        new(Account, Client, Broker, Instrument, OrderSide.Buy, 5m, 5m,
            OrderStatus.Filled, [Fill("execution-1", 2m), Fill("execution-2", 3m)]);

    private sealed class FailingSource : IBrokerReconciliationSource
    {
        public Task<BrokerOrderReconciliationEvidence> FetchOrderEvidenceAsync(
            BrokerOrderReconciliationContext context,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Synthetic source failure");
    }
}
