using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Ibkr;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class IbkrOfflineJournalReplayTests
{
    private const string Coid = "offline-trade-123";
    private const string Account = "synthetic-account-A";
    private const string BrokerId = "synthetic-broker-789";

    [Fact]
    public void EmptyJournal_RestoresPreparedWithoutTrading()
    {
        var snapshot = IbkrOfflineJournalReplay.Restore(Intent(),
            Array.Empty<IbkrOfflineJournalEvent>());
        Assert.Equal(IbkrOfflineOrderState.Prepared, snapshot.State);
        Assert.Equal(0m, snapshot.CumulativeFilled);
        Assert.Null(snapshot.BrokerOrderId);
    }

    [Fact]
    public void SubmittedBeforeCrash_RestoresUncertainState_NotEligibleToResubmit()
    {
        var events = new[]
        {
            Entry("preflight:approved", IbkrOfflineOrderEventType.PreflightApproved),
            Entry("placement:started", IbkrOfflineOrderEventType.SubmissionStarted)
        };

        var recovered = IbkrOfflineJournalReplay.Restore(Intent(), events);

        Assert.Equal(IbkrOfflineOrderState.AwaitingAcknowledgement, recovered.State);
        var model = new IbkrOfflineOrderLifecycle();
        model.Register(Intent());
        model.Replay(events.Select(e => e.Event));
        Assert.Throws<InvalidOperationException>(() =>
            model.Apply(new IbkrOfflineOrderEvent(Coid,
                IbkrOfflineOrderEventType.SubmissionStarted)));
    }

    [Fact]
    public void UnknownAfterCrash_StaysUnknown_NotFoundNeverUnlocksResubmit()
    {
        var events = new[]
        {
            Entry("preflight", IbkrOfflineOrderEventType.PreflightApproved),
            Entry("place", IbkrOfflineOrderEventType.SubmissionStarted),
            Entry("timeout", IbkrOfflineOrderEventType.TransportTimedOut),
            Entry("not-found", IbkrOfflineOrderEventType.ReconciliationNotFound)
        };

        var recovered = IbkrOfflineJournalReplay.Restore(Intent(), events);
        Assert.Equal(IbkrOfflineOrderState.Unknown, recovered.State);
        Assert.Equal(recovered, IbkrOfflineJournalReplay.Restore(Intent(), events));
        Assert.Throws<InvalidOperationException>(() => IbkrOfflineJournalReplay.Restore(
            Intent(), events.Append(Entry("unsafe-retry",
                IbkrOfflineOrderEventType.SubmissionStarted)).ToArray()));
    }

    [Fact]
    public void RecoveryOfPartiallyFilledAndCancelled_IsDeterministic()
    {
        var events = WorkingEvents().Append(new IbkrOfflineJournalEvent("fill:first",
            new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.CumulativeFill,
                CumulativeFilled: 2m))).Append(
            Entry("cancel:request", IbkrOfflineOrderEventType.CancelRequested)).Append(
            Entry("cancel:confirmed", IbkrOfflineOrderEventType.CancelConfirmed)).ToArray();
        var snapshot = IbkrOfflineJournalReplay.Restore(Intent(5m), events);
        Assert.Equal(IbkrOfflineOrderState.Cancelled, snapshot.State);
        Assert.Equal(2m, snapshot.CumulativeFilled);
        Assert.Equal(BrokerId, snapshot.BrokerOrderId);
    }

    [Fact]
    public void RecoveryAfterFullFill_MustNotRegressOnLateCancel()
    {
        var events = WorkingEvents().Append(new IbkrOfflineJournalEvent("fill:all",
            new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.CumulativeFill,
                CumulativeFilled: 5m))).Append(
            Entry("late-cancel", IbkrOfflineOrderEventType.CancelConfirmed)).ToArray();

        var snapshot = IbkrOfflineJournalReplay.Restore(Intent(5m), events);
        Assert.Equal(IbkrOfflineOrderState.Filled, snapshot.State);
        Assert.Equal(5m, snapshot.CumulativeFilled);
    }

    [Fact]
    public void DuplicateEventKey_FailsClosedEvenForIdenticalEvent()
    {
        var entry = Entry("preflight", IbkrOfflineOrderEventType.PreflightApproved);
        Assert.Throws<InvalidOperationException>(() => IbkrOfflineJournalReplay.Restore(
            Intent(), [entry, entry]));
    }

    [Fact]
    public void OutOfOrderOrContradictoryEventStream_FailsClosed()
    {
        var badSequences = new[]
        {
            new[] { Entry("placement", IbkrOfflineOrderEventType.SubmissionStarted) },
            new[]
            {
                Entry("preflight", IbkrOfflineOrderEventType.PreflightApproved),
                Entry("reject", IbkrOfflineOrderEventType.PreflightRejected)
            },
            new[]
            {
                Entry("preflight", IbkrOfflineOrderEventType.PreflightApproved),
                Entry("place", IbkrOfflineOrderEventType.SubmissionStarted),
                Entry("place-again", IbkrOfflineOrderEventType.SubmissionStarted)
            }
        };

        foreach (var events in badSequences)
        {
            Assert.Throws<InvalidOperationException>(() =>
                IbkrOfflineJournalReplay.Restore(Intent(), events));
        }
    }

    [Fact]
    public void CorruptFillOrWrongClientIdentity_FailsClosed()
    {
        var valid = WorkingEvents();
        var badEvents = new[]
        {
            new IbkrOfflineJournalEvent("fill:bad",
                new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.CumulativeFill,
                    CumulativeFilled: 6m)),
            new IbkrOfflineJournalEvent("wrong:client",
                new IbkrOfflineOrderEvent("another-client",
                    IbkrOfflineOrderEventType.CumulativeFill,
                    CumulativeFilled: 1m))
        };

        foreach (var bad in badEvents)
        {
            Assert.ThrowsAny<Exception>(() =>
                IbkrOfflineJournalReplay.Restore(Intent(5m), valid.Append(bad).ToArray()));
        }
    }

    [Fact]
    public void NoncanonicalOptionalFields_AreRejected()
    {
        var candidates = new[]
        {
            new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.PreflightApproved,
                BrokerOrderId: BrokerId),
            new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.BrokerAcknowledged),
            new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.HttpFailure),
            new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.BrokerAcknowledged,
                BrokerOrderId: BrokerId, HttpStatus: 200),
            new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.CumulativeFill),
            new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.ReplyRequired,
                ReplyId: " "),
            new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.ReconciledWorking,
                BrokerOrderId: BrokerId)
        };

        foreach (var candidate in candidates)
        {
            Assert.ThrowsAny<ArgumentException>(() =>
                IbkrOfflineJournalReplay.ValidateEvent(Intent(5m), "sample", candidate));
        }
    }

    [Theory]
    [InlineData("valid-key_01")]
    [InlineData("org.account:order-2")]
    [InlineData("A")]
    public void AllowedIdempotencyKeyFormats_AreStable(string key)
    {
        IbkrOfflineJournalReplay.ValidateKey(key, "key");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../../unsafe")]
    [InlineData("contains spaces")]
    [InlineData("line\nbreak")]
    [InlineData("unicode-😀")]
    public void UnsafeIdempotencyKeys_AreRejected(string key)
    {
        Assert.Throws<ArgumentException>(() =>
            IbkrOfflineJournalReplay.ValidateKey(key, "key"));
    }

    [Fact]
    public void OverlongKeyAndDecimalScale_FailBeforePostgres()
    {
        Assert.Throws<ArgumentException>(() =>
            IbkrOfflineJournalReplay.ValidateKey(new string('a', 129), "eventKey"));
        Assert.Throws<ArgumentException>(() =>
            IbkrOfflineJournalReplay.ValidateIntent(Intent(0m)));
        Assert.Throws<ArgumentException>(() =>
            IbkrOfflineJournalReplay.ValidateIntent(Intent(0.0000000000001m)));
        Assert.Throws<ArgumentException>(() =>
            IbkrOfflineJournalReplay.ValidateIntent(Intent(10_000_000_000_000_000m)));
    }

    [Fact]
    public void ConflictingIntentParameterAndEnum_AreNotAllowed()
    {
        Assert.Throws<ArgumentException>(() =>
            IbkrOfflineJournalReplay.ValidateIntent(Intent() with { AccountKey = " " }));
        Assert.Throws<ArgumentException>(() =>
            IbkrOfflineJournalReplay.ValidateIntent(Intent() with { ClientOrderId = "../path" }));
        Assert.Throws<ArgumentException>(() =>
            IbkrOfflineJournalReplay.ValidateIntent(Intent() with { Side = (OrderSide)999 }));
        Assert.Throws<ArgumentException>(() =>
            IbkrOfflineJournalReplay.ValidateIntent(Intent() with { Symbol = "" }));
    }

    [Fact]
    public void JournalReturnTypes_KeepIdsAndAmountsForRestartRecovery()
    {
        var evt = Entry("preflight", IbkrOfflineOrderEventType.PreflightApproved);
        var fill = new IbkrOfflineJournalExecution("exec:1", BrokerId, 1.5m);
        var recovered = new IbkrOfflineJournalRecovery(
            IbkrOfflineJournalReplay.Restore(Intent(), [evt]),
            [evt],
            [fill]);

        Assert.Equal(IbkrOfflineOrderState.PreflightApproved, recovered.Snapshot.State);
        Assert.Equal(evt, Assert.Single(recovered.Events));
        Assert.Equal(1.5m, Assert.Single(recovered.Executions).Quantity);
    }

    [Fact]
    public void PostgresJournalRequiresExplicitConnection_NotWiredByDefault()
    {
        Assert.Throws<ArgumentException>(() => new IbkrOfflinePostgresJournal(""));
        Assert.NotNull(new IbkrOfflinePostgresJournal(
            "Host=localhost;Database=explicit_synthetic_test;Username=offline;Password=unused"));
    }

    private static IbkrOfflineOrderIntent Intent(decimal quantity = 5m) =>
        new(Coid, Account, "AAPL", OrderSide.Buy, quantity);

    private static IbkrOfflineJournalEvent Entry(
        string key,
        IbkrOfflineOrderEventType type) =>
        new(key, new IbkrOfflineOrderEvent(Coid, type));

    private static IbkrOfflineJournalEvent[] WorkingEvents() =>
    [
        Entry("preflight", IbkrOfflineOrderEventType.PreflightApproved),
        Entry("place", IbkrOfflineOrderEventType.SubmissionStarted),
        new("broker-ack", new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.BrokerAcknowledged, BrokerOrderId: BrokerId))
    ];
}
