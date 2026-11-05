using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;
using static TradeOps.UnitTests.OmsRecoveryLedgerPostgresTests;

namespace TradeOps.UnitTests;

public sealed class DeribitRecoveryLedgerAssessmentTests
{
    [Theory]
    [InlineData(OmsRecoveryDispatchPhase.PersistedNotSent, 0, 1)]
    [InlineData(OmsRecoveryDispatchPhase.DispatchStarted, 2, 0)]
    [InlineData(OmsRecoveryDispatchPhase.Acknowledged, 1, 0)]
    [InlineData(OmsRecoveryDispatchPhase.Rejected, 1, 0)]
    [InlineData(OmsRecoveryDispatchPhase.Ambiguous, 2, 0)]
    public async Task QuietInbox_CannotHideSentOrAmbiguousCommands(OmsRecoveryDispatchPhase phase, int issues, int unsent)
    {
        var p = new Ports(); p.Snapshot = p.Snapshot with { Commands = [Row(phase)] };
        var result = await p.Gate().AssessWithLedgerAsync(p.Request(), p);
        Assert.Equal(1, result.JournalCommands); Assert.Equal(unsent, result.UnsentCommands);
        Assert.Equal(issues, result.CommandIssues.Count); Assert.Equal(1, p.LedgerReads);
        Assert.Equal(0, p.OldPrivateReads); Assert.Equal(1, p.QueryCalls); Assert.Equal(0, p.KnownCalls);
        Assert.False(result.AccountReady); Assert.False(result.DurableProjectionPublished);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.ExecutionLedgerCoverageUnproven, result.Orders.Blockers);
        Assert.Equal(issues > 0, result.Orders.Blockers.Contains(DeribitRecoveryEvidenceBlocker.CommandRecoveryUnresolved));
        if (phase is OmsRecoveryDispatchPhase.DispatchStarted or OmsRecoveryDispatchPhase.Ambiguous)
            Assert.Contains(result.CommandIssues, x => x.Problem == DeribitCommandRecoveryProblem.UnresolvedDispatch);
    }

    [Theory]
    [InlineData(OmsJournalCommandKind.Place, DeribitCommandRecoveryProblem.PlacementIdentityUnproven)]
    [InlineData(OmsJournalCommandKind.Cancel, DeribitCommandRecoveryProblem.MutationOutcomeUnproven)]
    [InlineData(OmsJournalCommandKind.Replace, DeribitCommandRecoveryProblem.MutationOutcomeUnproven)]
    public async Task AckAndExplicitTarget_DoNotProveVenueOutcome(OmsJournalCommandKind kind, DeribitCommandRecoveryProblem problem)
    {
        var p = new Ports(); var row = Row(OmsRecoveryDispatchPhase.Acknowledged);
        row = row with { Request = row.Request with { Payload = row.Request.Payload with {
            Kind = kind, TargetExchangeOrderId = "order-1", FixClOrdId = "label-is-not-proof" } } };
        p.Snapshot = p.Snapshot with { Commands = [row] };
        var result = await p.Gate().AssessWithLedgerAsync(p.Request(), p);
        Assert.Equal(problem, Assert.Single(result.CommandIssues).Problem);
    }

    [Fact]
    public async Task OtherInstrumentCommands_AreReportedInsteadOfFilteredOut()
    {
        var p = new Ports(); var row = Row(OmsRecoveryDispatchPhase.PersistedNotSent);
        p.Snapshot = p.Snapshot with { Commands = [row with { Request = row.Request with {
            Payload = row.Request.Payload with { Instrument = "BTC-PERPETUAL" } } }] };
        var result = await p.Gate().AssessWithLedgerAsync(p.Request(), p);
        Assert.Equal(DeribitCommandRecoveryProblem.InstrumentOutsideRecoveryScope, Assert.Single(result.CommandIssues).Problem);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.CommandRecoveryUnresolved, result.Orders.Blockers);
    }

    [Theory]
    [InlineData("scope")] [InlineData("snapshot")] [InlineData("stale")] [InlineData("future")]
    [InlineData("private-problem")] [InlineData("journal-problem")] [InlineData("command-scope")]
    [InlineData("attempt")] [InlineData("outbox")] [InlineData("duplicate")]
    [InlineData("version-gap")] [InlineData("zero-id")] [InlineData("capacity")]
    public async Task InvalidLedger_BlocksBeforeAnyVenueCall(string mode)
    {
        var p = new Ports(); var row = Row(OmsRecoveryDispatchPhase.PersistedNotSent);
        var foreign = new AccountKey(new("foreign"), Account.Venue, Account.Environment, Account.Account);
        var snapshot = p.Snapshot with { Commands = [row] };
        p.Snapshot = mode switch {
            "scope" => snapshot with { Private = snapshot.Private with { Scope = foreign } },
            "snapshot" => snapshot with { Private = snapshot.Private with { SnapshotId = Guid.Empty } },
            "stale" => snapshot with { Private = snapshot.Private with { ReadAtUtc = p.Now.AddMinutes(-1) } },
            "future" => snapshot with { Private = snapshot.Private with { ReadAtUtc = p.Now.AddMinutes(1) } },
            "private-problem" => snapshot with { Private = snapshot.Private with { Problems = [OmsPrivateRecoveryReadProblem.IntegrityIncident] } },
            "journal-problem" => snapshot with { Problems = [OmsRecoveryLedgerProblem.UnsupportedSchema] },
            "command-scope" => snapshot with { Commands = [row with { Request = row.Request with { Scope = foreign } }] },
            "attempt" => snapshot with { Commands = [row with { AttemptCount = 1 }] },
            "outbox" => snapshot with { Commands = [row with { Outbox = OmsRecoveryOutboxState.Resolved }] },
            "duplicate" => snapshot with { Commands = [row, row] },
            "version-gap" => snapshot with { Commands = [row with { AggregateVersion = 2, Request = row.Request with { ExpectedAggregateVersion = new(1) } }] },
            "zero-id" => snapshot with { Commands = [row with { Request = row.Request with { CommandId = default } }] },
            _ => snapshot with { Commands = Enumerable.Repeat(row, 10001).ToArray() }
        };
        var result = await p.Gate().AssessWithLedgerAsync(p.Request(), p);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.CommandLedgerInvalid, result.Orders.Blockers);
        Assert.Equal(0, result.JournalCommands); Assert.Empty(result.CommandIssues);
        Assert.Equal(0, p.QueryCalls); Assert.Equal(0, p.KnownCalls); Assert.Equal(0, p.OldPrivateReads);
    }

    [Fact]
    public async Task EmptyLedger_IsNotAccountCoverageOrReadyProof()
    {
        var p = new Ports(); var result = await p.Gate().AssessWithLedgerAsync(p.Request(), p);
        Assert.Equal(0, result.JournalCommands); Assert.Empty(result.CommandIssues);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.ExecutionLedgerCoverageUnproven, result.Orders.Blockers);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.CoherentCutUnavailable, result.Orders.Blockers);
        Assert.False(result.Orders.CanRebuildOrders);
    }

    [Fact]
    public async Task CancellationAndInvalidRequest_DoNotReadLedger()
    {
        var p = new Ports(); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.Gate().AssessWithLedgerAsync(p.Request(), p, stop.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => p.Gate().AssessWithLedgerAsync(p.Request() with {
            Query = p.Request().Query with { Epoch = Guid.Empty } }, p));
        Assert.Equal(0, p.LedgerReads); Assert.Equal(0, p.QueryCalls);
    }

    [Fact]
    public async Task ActualPostgresJournal_FeedsExistingGateWithoutSecondPrivateRead()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var journal = new OmsPostgresJournal(db.Source); var command = Command();
        await journal.PersistAsync(command); await journal.MarkDispatchStartedAsync(Account, command.CommandId);
        var p = new Ports(DateTimeOffset.UtcNow);
        var result = await p.Gate().AssessWithLedgerAsync(p.Request(), new OmsPostgresRecoveryLedgerReader(db.Source));
        Assert.Equal(1, result.JournalCommands); Assert.Equal(2, result.CommandIssues.Count);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.CommandRecoveryUnresolved, result.Orders.Blockers);
        Assert.DoesNotContain(DeribitRecoveryEvidenceBlocker.CommandLedgerInvalid, result.Orders.Blockers);
        Assert.Equal(0, p.OldPrivateReads); Assert.Equal(1, p.QueryCalls);
    }

    private static OmsRecoveryCommand Row(OmsRecoveryDispatchPhase phase)
    {
        var unsent = phase == OmsRecoveryDispatchPhase.PersistedNotSent;
        return new(Command(), 1, phase, unsent ? OmsRecoveryOutboxState.NotSent :
            phase is OmsRecoveryDispatchPhase.DispatchStarted or OmsRecoveryDispatchPhase.Ambiguous
                ? OmsRecoveryOutboxState.NeedsRecovery : OmsRecoveryOutboxState.Resolved,
            unsent ? 0 : 1, unsent ? null : phase);
    }

    private sealed class Ports : TimeProvider, IOmsRecoveryLedgerReader, IOmsPrivateRecoveryReader,
        IDeribitRecoveryQueries, IDeribitKnownOrderReader
    {
        internal readonly DateTimeOffset Now;
        internal OmsRecoveryLedgerSnapshot Snapshot;
        internal int LedgerReads, OldPrivateReads, QueryCalls, KnownCalls;
        internal Ports(DateTimeOffset? now = null)
        {
            Now = now ?? new DateTimeOffset(2026, 11, 5, 18, 0, 0, TimeSpan.Zero);
            Snapshot = new(new(Account, Guid.NewGuid(), Now, 0, [], []), [], []);
        }
        public override DateTimeOffset GetUtcNow() => Now;
        internal DeribitRecoveryGateRequest Request() => new(new(Account, Epoch, "ETH-PERPETUAL",
            Now.AddHours(-2), Now.AddSeconds(-10), TimeSpan.FromMinutes(2)), "ETH", OmsPrivateAmountUnit.Usd, "USD");
        internal DeribitRecoveryEvidenceGate Gate() => new(this, this, this, this);
        public Task<OmsRecoveryLedgerSnapshot> ReadAsync(AccountKey scope, int maximumRows = 10000, CancellationToken cancellationToken = default)
        { LedgerReads++; return Task.FromResult(Snapshot); }
        Task<OmsPrivateRecoveryReadResult> IOmsPrivateRecoveryReader.ReadAsync(AccountKey scope, int maximumRows, CancellationToken cancellationToken)
        { OldPrivateReads++; throw new InvalidOperationException("Must reuse the actual combined snapshot."); }
        public Task<DeribitRecoveryQueryResult> CollectAsync(DeribitRecoveryQuery q, CancellationToken ct = default)
        {
            QueryCalls++;
            return Task.FromResult(new DeribitRecoveryQueryResult(Account, Epoch, q.NativeInstrument, "ETH",
                DateTimeOffset.FromUnixTimeMilliseconds((q.FromUtc-q.Overlap).ToUnixTimeMilliseconds()),
                DateTimeOffset.FromUnixTimeMilliseconds(q.ThroughUtc.ToUnixTimeMilliseconds()), Now, Now,
                new[] { "open", "orders-recent", "orders-historical", "trades-recent", "trades-historical" }
                    .Select(x => new DeribitRecoverySourceRead(x, 1, true)).ToArray(), [], []));
        }
        public Task<DeribitKnownOrderReadResult> ReadAsync(DeribitKnownOrderReadRequest request, CancellationToken ct = default)
        { KnownCalls++; throw new InvalidOperationException("No retained order in this fixture."); }
    }
}
