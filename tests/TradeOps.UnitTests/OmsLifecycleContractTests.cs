using System;
using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class OmsLifecycleContractTests
{
    private static readonly AccountKey Account = new(new TenantId("tenant"),
        new VenueId("Deribit"), OmsEnvironment.Testnet, new TradingAccountId("account-1"));

    private static OmsOrderState NewOrder() =>
        OmsLifecycleContract.Create(
            new OmsOrderIdentity(Account, new OmsLocalOrderId(Guid.Parse(
                "11111111-1111-4111-8111-111111111111")),
                new OrderIntentId(Guid.Parse("22222222-2222-4222-8222-222222222222")),
                new ClientOrderLabel("intent-1"), null),
            new OmsOrderTerms("BTC-PERPETUAL", OrderSide.Buy,
                OrderType.Limit, 10m, OmsQuantityUnit.Contracts));

    private static OmsLifecycleObservation Event(OmsOrderState state, OmsOrderPhase phase,
        decimal cumulative = 0m, decimal? leaves = null,
        OmsObservationKind kind = OmsObservationKind.PrivateOrderReport,
        long? sequence = null, int revision = 0) =>
        new(state.Identity.Scope, state.Identity.LocalId, phase, cumulative, leaves,
            new OrderRevision(revision), kind,
            sequence.HasValue ? new OmsEventCursor("orders", 1, sequence.Value) : null);

    private static OmsOrderState Step(OmsOrderState state, OmsOrderPhase phase,
        decimal cumulative = 0m, decimal? leaves = null,
        OmsObservationKind kind = OmsObservationKind.PrivateOrderReport,
        long? sequence = null, int revision = 0)
    {
        var result = OmsLifecycleContract.Apply(state,
            Event(state, phase, cumulative, leaves, kind, sequence, revision));
        Assert.Equal(OmsTransitionOutcome.Applied, result.Outcome);
        Assert.Equal(state.Version.Value + 1, result.State.Version.Value);
        return result.State;
    }

    [Fact]
    public void HappyPath_ProgressesThroughAcceptanceAndPartialAndFill()
    {
        var state = NewOrder();
        state = Step(state, OmsOrderPhase.DispatchPending, kind: OmsObservationKind.LocalCommand);
        state = Step(state, OmsOrderPhase.Submitted, kind: OmsObservationKind.LocalCommand);
        state = Step(state, OmsOrderPhase.Accepted, sequence: 10);
        state = Step(state, OmsOrderPhase.PartiallyFilled, 4m, 6m, sequence: 11);
        state = Step(state, OmsOrderPhase.Filled, 10m, 0m, sequence: 12);

        Assert.True(OmsLifecycleContract.IsTerminal(state.Phase));
        Assert.Equal(10m, state.CumulativeFilled);
        Assert.Equal(0m, state.LeavesQuantity);
        Assert.Equal(5, state.Version.Value);
    }

    [Fact]
    public void SameSequence_IsIdempotent_ConflictingPayloadRequiresReconciliation()
    {
        var state = Step(NewOrder(), OmsOrderPhase.Accepted, sequence: 10);
        var duplicate = OmsLifecycleContract.Apply(state,
            Event(state, OmsOrderPhase.Accepted, sequence: 10));
        Assert.Equal(OmsTransitionOutcome.Duplicate, duplicate.Outcome);
        Assert.Same(state, duplicate.State);

        var collision = OmsLifecycleContract.Apply(state,
            Event(state, OmsOrderPhase.PartiallyFilled, 2m, sequence: 10));
        Assert.Equal(OmsTransitionOutcome.RequiresReconciliation, collision.Outcome);
        Assert.Same(state, collision.State);
    }

    [Fact]
    public void OutOfOrderEvents_DoNotRewindVersionOrQuantity()
    {
        var state = Step(NewOrder(), OmsOrderPhase.Accepted, sequence: 10);
        state = Step(state, OmsOrderPhase.PartiallyFilled, 5m, sequence: 12);

        var stale = OmsLifecycleContract.Apply(state,
            Event(state, OmsOrderPhase.PartiallyFilled, 2m, sequence: 11));
        Assert.Equal(OmsTransitionOutcome.Stale, stale.Outcome);
        Assert.Same(state, stale.State);

        var unsequencedRegression = OmsLifecycleContract.Apply(state,
            Event(state, OmsOrderPhase.PartiallyFilled, 3m));
        Assert.Equal(OmsTransitionOutcome.RequiresReconciliation, unsequencedRegression.Outcome);
        Assert.Same(state, unsequencedRegression.State);
    }

    [Fact]
    public void CancelPending_PartialFill_CancelConfirmed_ThenLateFillPreservesTerminalCancel()
    {
        var state = Step(NewOrder(), OmsOrderPhase.Accepted);
        state = Step(state, OmsOrderPhase.PartiallyFilled, 3m);
        state = Step(state, OmsOrderPhase.CancelPending, 3m, 7m,
            OmsObservationKind.LocalCommand);
        Assert.True(OmsLifecycleContract.IsRiskUnresolved(state.Phase));

        state = Step(state, OmsOrderPhase.PartiallyFilled, 5m, 5m,
            OmsObservationKind.ExecutionFill);
        state = Step(state, OmsOrderPhase.Cancelled, 5m, 0m);
        state = Step(state, OmsOrderPhase.Cancelled, 6m, 0m,
            OmsObservationKind.ExecutionFill);
        Assert.Equal(OmsOrderPhase.Cancelled, state.Phase);
        Assert.Equal(6m, state.CumulativeFilled);
        Assert.Equal(0m, state.LeavesQuantity);

        var conflictingFilled = OmsLifecycleContract.Apply(state,
            Event(state, OmsOrderPhase.Filled, 10m));
        Assert.Equal(OmsTransitionOutcome.RequiresReconciliation, conflictingFilled.Outcome);
        Assert.Same(state, conflictingFilled.State);
    }

    [Fact]
    public void FilledThenCancelledRace_IsNotAnIllegalTerminalRewrite()
    {
        var state = Step(NewOrder(), OmsOrderPhase.Accepted);
        state = Step(state, OmsOrderPhase.Filled, 10m);
        var delayedCancel = OmsLifecycleContract.Apply(state,
            Event(state, OmsOrderPhase.Cancelled, 10m, 0m));
        Assert.Equal(OmsTransitionOutcome.RequiresReconciliation, delayedCancel.Outcome);
        Assert.Equal(OmsOrderPhase.Filled, delayedCancel.State.Phase);
    }

    [Fact]
    public void RemoteOutcomeUnknown_PreventsBlindRetryAndRequiresConfirmedLookup()
    {
        var state = Step(NewOrder(), OmsOrderPhase.DispatchPending,
            kind: OmsObservationKind.LocalCommand);
        state = Step(state, OmsOrderPhase.Submitted,
            kind: OmsObservationKind.LocalCommand);
        state = Step(state, OmsOrderPhase.Unknown,
            kind: OmsObservationKind.LocalCommand);

        Assert.False(OmsDispatchSafety.IsProvablyNotSent(OmsCommandDispatchPhase.DispatchStarted));
        Assert.False(OmsDispatchSafety.IsProvablyNotSent(OmsCommandDispatchPhase.Ambiguous));
        Assert.True(OmsDispatchSafety.IsProvablyNotSent(OmsCommandDispatchPhase.PersistedNotSent));

        var retry = OmsLifecycleContract.Apply(state,
            Event(state, OmsOrderPhase.Submitted, kind: OmsObservationKind.LocalCommand));
        Assert.Equal(OmsTransitionOutcome.RequiresReconciliation, retry.Outcome);

        var openOrdersPresence = OmsLifecycleContract.Apply(state,
            Event(state, OmsOrderPhase.Accepted, kind: OmsObservationKind.OpenOrdersPresence));
        Assert.Equal(OmsTransitionOutcome.RequiresReconciliation, openOrdersPresence.Outcome);

        state = Step(state, OmsOrderPhase.PartiallyFilled, 4m,
            kind: OmsObservationKind.OrderLookup);
        Assert.Equal(4m, state.CumulativeFilled);
        Assert.Equal(6m, state.LeavesQuantity);
    }

    [Fact]
    public void EmptyOpenOrderSnapshot_DoesNotMakeOrderCancelled()
    {
        var state = Step(NewOrder(), OmsOrderPhase.Accepted);
        var result = OmsLifecycleContract.MissingFromOpenOrderSnapshot(state);
        Assert.Equal(OmsTransitionOutcome.RequiresReconciliation, result.Outcome);
        Assert.Same(state, result.State);
    }

    [Fact]
    public void LocalCommandCannotAssertExchangeFillOrCancel()
    {
        var state = Step(NewOrder(), OmsOrderPhase.Accepted);
        var bad = OmsLifecycleContract.Apply(state,
            Event(state, OmsOrderPhase.Filled, 10m,
                kind: OmsObservationKind.LocalCommand));
        Assert.Equal(OmsTransitionOutcome.RequiresReconciliation, bad.Outcome);
    }

    [Fact]
    public void ReplacePending_RevisionMustBeMonotonicAndExplicit()
    {
        var state = Step(NewOrder(), OmsOrderPhase.Accepted);
        var invalid = OmsLifecycleContract.Apply(state,
            Event(state, OmsOrderPhase.Accepted, revision: 1));
        Assert.Equal(OmsTransitionOutcome.RequiresReconciliation, invalid.Outcome);

        state = Step(state, OmsOrderPhase.ReplacePending,
            kind: OmsObservationKind.LocalCommand);
        state = Step(state, OmsOrderPhase.Accepted, revision: 1);
        Assert.Equal(1, state.Revision.Value);

        var stale = OmsLifecycleContract.Apply(state,
            Event(state, OmsOrderPhase.Accepted, revision: 0));
        Assert.Equal(OmsTransitionOutcome.Stale, stale.Outcome);
    }

    [Fact]
    public void QuantityAndUnitInvariants_RefuseFalseFilledOrLeaves()
    {
        var state = Step(NewOrder(), OmsOrderPhase.Accepted);
        Assert.Equal(OmsTransitionOutcome.RequiresReconciliation,
            OmsLifecycleContract.Apply(state,
                Event(state, OmsOrderPhase.Filled, 9m)).Outcome);
        Assert.Equal(OmsTransitionOutcome.RequiresReconciliation,
            OmsLifecycleContract.Apply(state,
                Event(state, OmsOrderPhase.PartiallyFilled, 4m, 9m)).Outcome);
        Assert.Equal(OmsTransitionOutcome.RequiresReconciliation,
            OmsLifecycleContract.Apply(state,
                Event(state, OmsOrderPhase.PartiallyFilled, 11m)).Outcome);
    }

    [Fact]
    public void IdentityScopeMismatch_ThrowsWithoutMutating()
    {
        var state = NewOrder();
        var otherScope = new AccountKey(new TenantId("tenant"), new VenueId("Deribit"),
            OmsEnvironment.Testnet, new TradingAccountId("account-2"));
        var wrong = Event(state, OmsOrderPhase.Accepted) with { Scope = otherScope };
        Assert.Throws<InvalidOperationException>(() => OmsLifecycleContract.Apply(state, wrong));
        Assert.Equal(OmsOrderPhase.Created, state.Phase);
    }

    [Fact]
    public void FirstAuthoritativeRemoteId_BindsOnce_AndConflictingRebindIsQuarantined()
    {
        var state = NewOrder();
        var first = Event(state, OmsOrderPhase.Accepted) with
        {
            ExchangeId = new ExchangeOrderKey("ex-101"),
            ClientLabel = new ClientOrderLabel("intent-1")
        };
        var bound = OmsLifecycleContract.Apply(state, first);
        Assert.Equal(OmsTransitionOutcome.Applied, bound.Outcome);
        state = bound.State;
        Assert.Equal("ex-101", state.Identity.ExchangeId!.Value.Value);

        var conflict = Event(state, OmsOrderPhase.PartiallyFilled, 3m) with
        {
            ExchangeId = new ExchangeOrderKey("ex-102")
        };
        var result = OmsLifecycleContract.Apply(state, conflict);
        Assert.Equal(OmsTransitionOutcome.RequiresReconciliation, result.Outcome);
        Assert.Same(state, result.State);
        Assert.Equal("ex-101", result.State.Identity.ExchangeId!.Value.Value);
    }

    [Fact]
    public void LocalCommandCannotBindExchangeOrderId()
    {
        var state = NewOrder();
        var command = Event(state, OmsOrderPhase.DispatchPending,
            kind: OmsObservationKind.LocalCommand) with
        {
            ExchangeId = new ExchangeOrderKey("invented-id")
        };
        Assert.Equal(OmsTransitionOutcome.RequiresReconciliation,
            OmsLifecycleContract.Apply(state, command).Outcome);
    }

    [Fact]
    public void CursorEpochChange_NeedsExplicitRecoveryGate()
    {
        var state = Step(NewOrder(), OmsOrderPhase.Accepted, sequence: 100);
        var newEpoch = Event(state, OmsOrderPhase.PartiallyFilled, 2m) with
        {
            Cursor = new OmsEventCursor("orders", 2, 1)
        };
        Assert.Equal(OmsTransitionOutcome.RequiresReconciliation,
            OmsLifecycleContract.Apply(state, newEpoch).Outcome);
    }
}
