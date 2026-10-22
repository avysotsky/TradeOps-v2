using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class BrokerReconciliationDecisionEngineTests
{
    private const string Provider = "synthetic-venue";
    private const string Account = "synthetic-paper-account";
    private const string ClientId = "coid-111";
    private const string BrokerId = "broker-222";
    private const string Instrument = "STK:265598:USD:SMART";

    private readonly IBrokerReconciliationDecisionEngine _engine =
        new BrokerReconciliationDecisionEngine(new OrderStateMachine());

    [Fact]
    public void CompletedEvidence_ProposesFillCandidate_WithoutBrokerMutation()
    {
        var evaluation = _engine.Evaluate(Local(), Found());

        Assert.Equal(BrokerReconciliationDecisionKind.Candidate, evaluation.Kind);
        Assert.Equal(OrderStatus.Filled, evaluation.CandidateStatus);
        Assert.Equal(5m, evaluation.CandidateFilledQuantity);
    }

    [Fact]
    public void IdenticalExecutionIdReplay_CountsOnce()
    {
        var row = Fill("exec-1", 5m);
        var evidence = Found(Order(executions: [row, row]));

        var result = _engine.Evaluate(Local(), evidence);
        Assert.Equal(BrokerReconciliationDecisionKind.Candidate, result.Kind);
        Assert.Equal(5m, result.CandidateFilledQuantity);
    }

    [Fact]
    public void ConflictingDuplicateExecutionId_FailsClosed()
    {
        var bad = Found(Order(executions:
        [
            Fill("exec-1", 2m),
            Fill("exec-1", 3m)
        ]));
        Assert.Throws<InvalidOperationException>(() =>
            _engine.Evaluate(Local(), bad));
    }

    [Fact]
    public void CompleteNotFound_IsStillUnresolved_AndNotAResubmitPermission()
    {
        var result = _engine.Evaluate(
            Local(),
            new(BrokerEvidenceAvailability.NotFound, Provider, Account,
                ObservationSequence: 7, OrdersComplete: true,
                ExecutionsComplete: false, Order: null));

        Assert.Equal(BrokerReconciliationDecisionKind.Unresolved, result.Kind);
        Assert.Null(result.CandidateStatus);
        Assert.Null(result.CandidateFilledQuantity);
        Assert.Contains("NotFound", result.ReasonCode, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BrokerEvidenceAvailability.Unauthorized)]
    [InlineData(BrokerEvidenceAvailability.Unavailable)]
    [InlineData(BrokerEvidenceAvailability.RateLimited)]
    public void TransportOrSessionFailure_IsNotMissingOrder(
        BrokerEvidenceAvailability availability)
    {
        var result = _engine.Evaluate(
            Local(),
            new(availability, Provider, Account, null, false, false, null));

        Assert.Equal(BrokerReconciliationDecisionKind.Unresolved, result.Kind);
        Assert.Null(result.CandidateStatus);
        Assert.Null(result.CandidateFilledQuantity);
    }

    [Fact]
    public void IncompleteOrderPages_DoNotResolveUnknown()
    {
        var evidence = Found() with
        {
            Availability = BrokerEvidenceAvailability.Incomplete,
            OrdersComplete = false
        };
        var result = _engine.Evaluate(Local(), evidence);

        Assert.Equal(BrokerReconciliationDecisionKind.Unresolved, result.Kind);
        Assert.Null(result.CandidateStatus);
    }

    [Fact]
    public void IncompleteExecutions_DoNotResolveEvenWithFullOrderList()
    {
        var evidence = Found() with { ExecutionsComplete = false };
        var result = _engine.Evaluate(Local(), evidence);

        Assert.Equal(BrokerReconciliationDecisionKind.Unresolved, result.Kind);
        Assert.Null(result.CandidateFilledQuantity);
    }

    [Fact]
    public void AlreadyAlignedUnfilledKnownOrder_ReturnsNoChange()
    {
        var ctx = Local() with
        {
            BrokerOrderId = BrokerId,
            LocalStatus = OrderStatus.Submitted,
            LocalFilledQuantity = 0m
        };
        var result = _engine.Evaluate(ctx, Found(
            Order(OrderStatus.Submitted, 0m, [])));

        Assert.Equal(BrokerReconciliationDecisionKind.NoChange, result.Kind);
        Assert.Null(result.CandidateStatus);
    }

    [Fact]
    public void AlignedPositiveFills_StillNeedDurableExecutionIdVerification()
    {
        var ctx = Local() with
        {
            BrokerOrderId = BrokerId,
            LocalStatus = OrderStatus.Filled,
            LocalFilledQuantity = 5m
        };
        var result = _engine.Evaluate(ctx, Found());

        Assert.Equal(BrokerReconciliationDecisionKind.Candidate, result.Kind);
        Assert.Equal(OrderStatus.Filled, result.CandidateStatus);
        Assert.Equal(5m, result.CandidateFilledQuantity);
    }

    [Fact]
    public void MissingBrokerOrderId_RemainsCandidateEvenIfStatusIsAligned()
    {
        var ctx = Local() with
        {
            LocalStatus = OrderStatus.Filled,
            LocalFilledQuantity = 5m
        };
        var result = _engine.Evaluate(ctx, Found());

        Assert.Equal(BrokerReconciliationDecisionKind.Candidate, result.Kind);
        Assert.Equal(OrderStatus.Filled, result.CandidateStatus);
    }

    [Fact]
    public void CrossProviderAndAccountEvidence_MustNeverBeAccepted()
    {
        foreach (var evidence in new[]
        {
            Found() with { ProviderKey = "other-venue" },
            Found() with { AccountKey = "other-account" },
            Found(Order() with { AccountKey = "other-account" })
        })
        {
            Assert.Throws<InvalidOperationException>(() =>
                _engine.Evaluate(Local(), evidence));
        }
    }

    [Fact]
    public void CrossOrderInstrumentOrSideEvidence_FailsClosed()
    {
        foreach (var order in new[]
        {
            Order() with { ClientOrderId = "different-coid" },
            Order() with { BrokerOrderId = "different-broker-id" },
            Order() with { InstrumentKey = "STK:MSFT:USD:SMART" },
            Order() with { Side = OrderSide.Sell },
            Order() with { RequestedQuantity = 6m }
        })
        {
            var context = Local() with { BrokerOrderId = BrokerId };
            Assert.Throws<InvalidOperationException>(() =>
                _engine.Evaluate(context, Found(order)));
        }
    }

    [Fact]
    public void ExecutionMustMatchEveryBoundOrderIdentity()
    {
        foreach (var row in new[]
        {
            Fill("exec-1", 5m) with { AccountKey = "wrong-account" },
            Fill("exec-1", 5m) with { ClientOrderId = "wrong-client" },
            Fill("exec-1", 5m) with { BrokerOrderId = "wrong-broker" },
            Fill("exec-1", 5m) with { ExecutionId = " " },
            Fill("exec-1", 5m) with { Quantity = -1m },
            Fill("exec-1", 5m) with { Quantity = 0m }
        })
        {
            Assert.Throws<InvalidOperationException>(() =>
                _engine.Evaluate(Local(), Found(Order(executions: [row]))));
        }
    }

    [Fact]
    public void QuantityMustBeExactMonotonicAndComplete()
    {
        foreach (var order in new[]
        {
            Order() with { CumulativeFilledQuantity = -1m },
            Order() with { CumulativeFilledQuantity = 6m },
            Order() with { CumulativeFilledQuantity = 4m },
            Order() with { CumulativeFilledQuantity = 0.0000000000001m },
            Order(executions: [Fill("exec-1", 1m)]),
            Order() with { Status = OrderStatus.PartiallyFilled },
            Order() with { Status = OrderStatus.Created }
        })
        {
            Assert.Throws<InvalidOperationException>(() =>
                _engine.Evaluate(Local(), Found(order)));
        }

        Assert.Throws<InvalidOperationException>(() =>
            _engine.Evaluate(Local() with { LocalFilledQuantity = 5m },
                Found(Order(OrderStatus.PartiallyFilled, 3m,
                    [Fill("exec-1", 3m)]))));
    }

    [Fact]
    public void StaleSequenceOrSameSequence_RejectsWithoutDigestProof()
    {
        foreach (var sequence in new long?[] { null, 0, 6, 7 })
        {
            Assert.Throws<InvalidOperationException>(() =>
                _engine.Evaluate(
                    Local() with { LastAcceptedObservationSequence = 7 },
                    Found() with { ObservationSequence = sequence }));
        }
        Assert.Equal(BrokerReconciliationDecisionKind.Candidate,
            _engine.Evaluate(Local() with { LastAcceptedObservationSequence = 7 },
                Found() with { ObservationSequence = 8 }).Kind);
    }

    [Fact]
    public void UnknownAndTerminalRejection_DoNotBecomeAutomaticMutations()
    {
        foreach (var status in new[] { OrderStatus.Unknown, OrderStatus.Rejected })
        {
            var result = _engine.Evaluate(
                Local(), Found(Order(status: status)));

            Assert.Equal(BrokerReconciliationDecisionKind.Unresolved, result.Kind);
            Assert.Null(result.CandidateStatus);
        }
    }

    [Fact]
    public void WorkingStatusWithFills_IsAmbiguousUntilNormalized()
    {
        var evidence = Found(Order(
            OrderStatus.Submitted, 2m, [Fill("exec-1", 2m)]));
        var result = _engine.Evaluate(Local(), evidence);
        Assert.Equal(BrokerReconciliationDecisionKind.Unresolved, result.Kind);
    }

    [Fact]
    public void InvalidTransition_DoesNotProduceCandidate()
    {
        var context = Local() with
        {
            LocalStatus = OrderStatus.Cancelled,
            LocalFilledQuantity = 2m
        };
        var result = _engine.Evaluate(context, Found());
        Assert.Equal(BrokerReconciliationDecisionKind.Unresolved, result.Kind);
        Assert.Null(result.CandidateStatus);
    }

    [Fact]
    public void MalformedCompletenessOrAvailability_RejectsAmbiguousEvidence()
    {
        var cases = new[]
        {
            Found() with { Order = null },
            Found() with { Availability = BrokerEvidenceAvailability.NotFound },
            Found() with { Availability = (BrokerEvidenceAvailability)999 },
            Found() with { Availability = BrokerEvidenceAvailability.Incomplete },
            Found() with { Availability = BrokerEvidenceAvailability.Unavailable },
            Found() with { Availability = BrokerEvidenceAvailability.RateLimited },
            Found() with { Availability = BrokerEvidenceAvailability.Unauthorized }
        };
        foreach (var evidence in cases)
        {
            Assert.Throws<InvalidOperationException>(() =>
                _engine.Evaluate(Local(), evidence));
        }
    }

    [Fact]
    public void NormalizedSourceContract_IsReadOnlyByConstruction()
    {
        IBrokerReconciliationSource source = new CannedSource(Found());
        var observation = source.FetchOrderEvidenceAsync(Local())
            .GetAwaiter().GetResult();

        Assert.Equal(BrokerEvidenceAvailability.Complete, observation.Availability);
        Assert.Equal(7L, observation.ObservationSequence);
    }

    [Fact]
    public void SubmittedAndPartialFills_OnlyValidWithCorrectQuantities()
    {
        var submitted = _engine.Evaluate(Local(), Found(
            Order(OrderStatus.Submitted, 0m, [])));
        Assert.Equal(OrderStatus.Submitted, submitted.CandidateStatus);

        var partial = _engine.Evaluate(Local(), Found(
            Order(OrderStatus.PartiallyFilled, 2m, [Fill("exec-1", 2m)])));
        Assert.Equal(OrderStatus.PartiallyFilled, partial.CandidateStatus);
        Assert.Equal(2m, partial.CandidateFilledQuantity);

        Assert.Throws<InvalidOperationException>(() =>
            _engine.Evaluate(Local(), Found(
                Order(OrderStatus.Filled, 2m, [Fill("exec-1", 2m)]))));
    }

    private static BrokerOrderReconciliationContext Local() =>
        new(Provider, Account, ClientId, null, Instrument,
            OrderSide.Buy, RequestedQuantity: 5m,
            LocalStatus: OrderStatus.Unknown,
            LocalFilledQuantity: 0m, LastAcceptedObservationSequence: 0);

    private static NormalizedBrokerExecutionEvidence Fill(
        string id, decimal quantity) =>
        new(id, Account, ClientId, BrokerId, quantity);

    private static NormalizedBrokerOrderEvidence Order(
        OrderStatus status = OrderStatus.Filled,
        decimal filled = 5m,
        IReadOnlyList<NormalizedBrokerExecutionEvidence>? executions = null) =>
        new(Account, ClientId, BrokerId, Instrument, OrderSide.Buy,
            RequestedQuantity: 5m, CumulativeFilledQuantity: filled,
            Status: status,
            Executions: executions ?? [Fill("exec-1", 2m), Fill("exec-2", 3m)]);

    private static BrokerOrderReconciliationEvidence Found(
        NormalizedBrokerOrderEvidence? order = null) =>
        new(BrokerEvidenceAvailability.Complete, Provider, Account,
            ObservationSequence: 7, OrdersComplete: true,
            ExecutionsComplete: true, Order: order ?? Order());

    private sealed class CannedSource(BrokerOrderReconciliationEvidence evidence)
        : IBrokerReconciliationSource
    {
        public Task<BrokerOrderReconciliationEvidence> FetchOrderEvidenceAsync(
            BrokerOrderReconciliationContext order,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(evidence);
        }
    }
}
