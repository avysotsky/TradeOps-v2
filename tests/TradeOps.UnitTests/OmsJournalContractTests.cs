using System.Security.Cryptography;
using TradeOps.Application.Oms;
using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class OmsJournalContractTests
{
    private static AccountKey Account => new(new TenantId("tenant"), new VenueId("Deribit"),
        OmsEnvironment.Testnet, new TradingAccountId("trading-1"));

    private static OmsJournalCommandRequest Request() => new(
        Account,
        new OmsLocalOrderId(Guid.Parse("11111111-1111-4111-8111-111111111111")),
        new OrderIntentId(Guid.Parse("22222222-2222-4222-8222-222222222222")),
        new OmsCommandId(Guid.Parse("33333333-3333-4333-8333-333333333333")),
        new OrderVersion(0),
        new OmsJournalCommandPayload(
            OmsJournalCommandKind.Place, "BTC-PERPETUAL", OrderSide.Buy, OrderType.Limit,
            10m, OmsQuantityUnit.Contracts, 62000m, null, "GTC", false, true,
            "test-fixture", "strategy-A", "risk-decision-1",
            new DateTimeOffset(2026, 10, 25, 17, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void Digest_IsDeterministicAndVersioned_WithFullCommandPayload()
    {
        var request = Request();
        var first = OmsJournalCanonical.EncodeCommand(request);
        var second = OmsJournalCanonical.EncodeCommand(request);
        Assert.Equal(32, first.Sha256.Length);
        Assert.True(first.Sha256.SequenceEqual(second.Sha256));
        Assert.Contains("\"schemaVersion\":1", first.Json);
        Assert.Contains("\"postOnly\":true", first.Json);
        Assert.Contains("\"riskDecisionReference\":\"risk-decision-1\"", first.Json);
        var changed = OmsJournalCanonical.EncodeCommand(request with
        {
            Payload = request.Payload with { ReduceOnly = true }
        });
        Assert.False(first.Sha256.SequenceEqual(changed.Sha256));
        Assert.Equal(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(first.Json)), first.Sha256);
    }

    [Fact]
    public void InvalidOrMissingParameters_AreRejectedBeforeAnyDatabaseIO()
    {
        var request = Request();
        Assert.Throws<ArgumentOutOfRangeException>(() => OmsJournalCanonical.EncodeCommand(request with
        {
            Payload = request.Payload with { Quantity = 0m }
        }));
        Assert.Throws<ArgumentException>(() => OmsJournalCanonical.EncodeCommand(request with
        {
            Payload = request.Payload with { RiskDecisionReference = " " }
        }));
        Assert.Throws<ArgumentException>(() => OmsJournalCanonical.EncodeCommand(request with
        {
            Payload = request.Payload with { RequestedAtUtc = DateTimeOffset.Now }
        }));
        Assert.Throws<ArgumentException>(() => OmsJournalCanonical.EncodeCommand(request with
        {
            Payload = request.Payload with { Instrument = new string('X', 161) }
        }));
    }

    [Fact]
    public void RemoteInboxDigest_IsAccountScopedAndPayloadSensitive()
    {
        var first = new OmsJournalInboxEvent(Account, "FIX:session-1", "execution-1",
            OmsJournalEventKind.Execution, Request().LocalOrderId, "BTC-PERPETUAL",
            3m, 64000m, new DateTimeOffset(2026, 10, 25, 17, 0, 0, TimeSpan.Zero));
        var other = first with { Scope = new AccountKey(
            new TenantId("tenant"), new VenueId("Deribit"), OmsEnvironment.Testnet,
            new TradingAccountId("trading-2")) };
        var changed = first with { Price = 64001m };
        Assert.False(OmsJournalCanonical.EncodeInbox(first).Sha256.SequenceEqual(
            OmsJournalCanonical.EncodeInbox(other).Sha256));
        Assert.False(OmsJournalCanonical.EncodeInbox(first).Sha256.SequenceEqual(
            OmsJournalCanonical.EncodeInbox(changed).Sha256));
    }

    [Fact]
    public void B1AcceptedWithNonZeroCumulativeAfterCancelPending_StillRequiresReconciliation()
    {
        // B1 review item from Issue #67: do NOT adjust the parent B1 reducer in B2.
        var terms = new OmsOrderTerms("BTC-PERPETUAL", OrderSide.Buy, OrderType.Limit,
            10m, OmsQuantityUnit.Contracts);
        var identity = new OmsOrderIdentity(Account, Request().LocalOrderId,
            Request().IntentId, new ClientOrderLabel("label-1"), null);
        var state = OmsLifecycleContract.Create(identity, terms);
        state = OmsLifecycleContract.Apply(state, new OmsLifecycleObservation(
            Account, identity.LocalId, OmsOrderPhase.Accepted, 0m, 10m,
            new OrderRevision(0), OmsObservationKind.OrderLookup)).State;
        state = OmsLifecycleContract.Apply(state, new OmsLifecycleObservation(
            Account, identity.LocalId, OmsOrderPhase.PartiallyFilled, 3m, 7m,
            new OrderRevision(0), OmsObservationKind.ExecutionFill)).State;
        state = OmsLifecycleContract.Apply(state, new OmsLifecycleObservation(
            Account, identity.LocalId, OmsOrderPhase.CancelPending, 3m, 7m,
            new OrderRevision(0), OmsObservationKind.LocalCommand)).State;
        var result = OmsLifecycleContract.Apply(state, new OmsLifecycleObservation(
            Account, identity.LocalId, OmsOrderPhase.Accepted, 3m, 7m,
            new OrderRevision(0), OmsObservationKind.OrderHistory));
        Assert.Equal(OmsTransitionOutcome.RequiresReconciliation, result.Outcome);
        Assert.Same(state, result.State);
    }
}
