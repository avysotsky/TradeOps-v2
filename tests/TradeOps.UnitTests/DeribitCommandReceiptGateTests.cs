using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.DeribitCommandReceiptFixtures;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitCommandReceiptGateTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PlacementAndCancelNeedMatchingTerminalPrivateEvidence(bool contradictCancel)
    {
        var p = new Ports(); var place = p.Receipt;
        var cancel = Receipt(Cancel(place.Command), rpcId: 2, sent: Now.AddSeconds(-20));
        var row = p.Snapshot.Private.Observations[0];
        p.Snapshot = p.Snapshot with { Commands = [Row(place.Command), Row(cancel.Command)], CommandReceipts = [place, cancel],
            Private = p.Snapshot.Private with { Observations = [row with { Envelope = row.Envelope with {
                Facts = cancel.Order with { VenueTimestampUtc = Now.AddSeconds(-15), OrderState = contradictCancel ? "open" : "cancelled" },
                Observation = row.Envelope.Observation with { ReceivedAtUtc = Now.AddSeconds(-10) } } }] } };
        var result = await p.Gate().AssessWithLedgerAsync(p.Request(), p);
        Assert.Equal(2, result.BoundCommands);
        if (contradictCancel) Assert.Equal(DeribitCommandRecoveryProblem.BoundOrderEvidenceMismatch, Assert.Single(result.CommandIssues).Problem);
        else Assert.Empty(result.CommandIssues);
        Assert.False(result.AccountReady);
    }

    [Theory]
    [InlineData(OmsRecoveryDispatchPhase.DispatchStarted)]
    [InlineData(OmsRecoveryDispatchPhase.Ambiguous)]
    [InlineData(OmsRecoveryDispatchPhase.Acknowledged)]
    public async Task DirectReceiptAndActualPrivateReportResolveOnlyCommandSpecificIssues(OmsRecoveryDispatchPhase phase)
    {
        var p = new Ports(); p.Snapshot = p.Snapshot with { Commands = [Row(p.Receipt.Command, phase)] };
        var result = await p.Gate().AssessWithLedgerAsync(p.Request(), p);
        Assert.Equal(1, result.BoundCommands); Assert.Empty(result.CommandIssues);
        Assert.DoesNotContain(DeribitRecoveryEvidenceBlocker.CommandRecoveryUnresolved, result.Orders.Blockers);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.ExecutionLedgerCoverageUnproven, result.Orders.Blockers);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.CoherentCutUnavailable, result.Orders.Blockers);
        Assert.False(result.AccountReady); Assert.False(result.Orders.CanRebuildOrders);
        Assert.Equal(1, p.QueryCalls); Assert.Equal(1, p.KnownCalls);
    }

    [Fact]
    public async Task ReceiptWithoutAcceptedNativeReportIsNotCompleteRecoveryEvidence()
    {
        var p = new Ports(); p.Snapshot = p.Snapshot with { Private = p.Snapshot.Private with { AcceptedFactCount = 0, Observations = [] } };
        var result = await p.Gate().AssessWithLedgerAsync(p.Request(), p);
        Assert.Equal(1, result.BoundCommands);
        Assert.Equal(DeribitCommandRecoveryProblem.BoundOrderEvidenceUnavailable, Assert.Single(result.CommandIssues).Problem);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.CommandRecoveryUnresolved, result.Orders.Blockers);
    }

    [Theory]
    [InlineData("side")] [InlineData("amount")] [InlineData("price")] [InlineData("missing-cumulative")]
    public async Task AcceptedReportCannotBeMappedByIdWhenNativeTermsDiffer(string mode)
    {
        var p = new Ports(); var row = p.Snapshot.Private.Observations[0];
        var facts = mode switch { "side" => row.Envelope.Facts with { Side = TradeOps.Domain.Enums.OrderSide.Sell },
            "amount" => row.Envelope.Facts with { NativeAmount = 2 },
            "missing-cumulative" => row.Envelope.Facts with { FilledNativeAmount = null },
            _ => row.Envelope.Facts with { Price = 1999 } };
        p.Snapshot = p.Snapshot with { Private = p.Snapshot.Private with { Observations = [row with {
            Envelope = row.Envelope with { Facts = facts } }] } };
        var result = await p.Gate().AssessWithLedgerAsync(p.Request(), p);
        Assert.Equal(DeribitCommandRecoveryProblem.BoundOrderEvidenceMismatch, Assert.Single(result.CommandIssues).Problem);
    }

    [Fact]
    public async Task ExecutionPriceAndPartialAmountAreNotMistakenForImmutableOrderTerms()
    {
        var p = new Ports(); var row = p.Snapshot.Private.Observations[0];
        var execution = Fill(0.5m) with { Facts = Fill(0.5m).Facts with { ExchangeOrderId = p.Receipt.Order.ExchangeOrderId,
            Price = 1990, VenueTimestampUtc = p.Receipt.Order.VenueTimestampUtc.AddSeconds(1) },
            Observation = row.Envelope.Observation with { CaptureId = Guid.NewGuid(), ReceivedAtUtc = Now.AddSeconds(-10) } };
        p.Snapshot = p.Snapshot with { Private = p.Snapshot.Private with { AcceptedFactCount = 2, Observations = [row,
            new(execution, OmsPrivateCaptureResult.Inserted)] } };
        var result = await p.Gate().AssessWithLedgerAsync(p.Request(), p);
        Assert.Empty(result.CommandIssues); Assert.Equal(1, result.BoundCommands);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.ExecutionLedgerCoverageUnproven, result.Orders.Blockers);
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("foreign")] [InlineData("command-changed")]
    [InlineData("unsent")] [InlineData("rejected")] [InlineData("orphan-cancel")]
    [InlineData("rpc-reuse")] [InlineData("native-reuse")]
    public async Task ForgedOrIncompleteReceiptSetBlocksBeforeVenueIo(string mode)
    {
        var p = new Ports(); var r = p.Receipt;
        switch (mode)
        {
            case "duplicate": p.Snapshot = p.Snapshot with { CommandReceipts = [r, r] }; break;
            case "foreign": p.Snapshot = p.Snapshot with { CommandReceipts = [r with { Command = r.Command with {
                Scope = new AccountKey(new("foreign"), Account.Venue, Account.Environment, Account.Account) } }] }; break;
            case "command-changed": p.Snapshot = p.Snapshot with { CommandReceipts = [r with { Command = r.Command with {
                Payload = r.Command.Payload with { RiskDecisionReference = "changed" } } }] }; break;
            case "unsent": p.Snapshot = p.Snapshot with { Commands = [Row(r.Command, OmsRecoveryDispatchPhase.PersistedNotSent)] }; break;
            case "rejected": p.Snapshot = p.Snapshot with { Commands = [Row(r.Command, OmsRecoveryDispatchPhase.Rejected)] }; break;
            case "orphan-cancel": var cancel = Receipt(Cancel(r.Command)); p.Snapshot = p.Snapshot with {
                Commands = [Row(r.Command), Row(cancel.Command)], CommandReceipts = [cancel] }; break;
            default:
                var other = Receipt(Command(), mode == "rpc-reuse" ? "native-2" : r.Order.ExchangeOrderId,
                    mode == "rpc-reuse" ? r.RpcRequestId : 2, session: r.SessionId);
                p.Snapshot = p.Snapshot with { Commands = [Row(r.Command), Row(other.Command)], CommandReceipts = [r, other] }; break;
        }
        var result = await p.Gate().AssessWithLedgerAsync(p.Request(), p);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.CommandLedgerInvalid, result.Orders.Blockers);
        Assert.Equal(0, p.QueryCalls); Assert.Equal(0, p.KnownCalls); Assert.Equal(0, result.BoundCommands);
    }

    [Fact]
    public async Task LegacySnapshotWithoutReceiptsKeepsItsUnresolvedCommand()
    {
        var p = new Ports(); p.Snapshot = p.Snapshot with { CommandReceipts = null };
        var result = await p.Gate().AssessWithLedgerAsync(p.Request(), p);
        Assert.Equal(0, result.BoundCommands); Assert.Equal(2, result.CommandIssues.Count);
        Assert.Contains(result.CommandIssues, x => x.Problem == DeribitCommandRecoveryProblem.PlacementIdentityUnproven);
    }

    [Fact]
    public async Task ActualPostgresReceiptAndPrivateCaptureFeedExistingOrderGate()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        var r = await DeribitCommandReceiptPostgresTests.Start(db);
        await new OmsPostgresDeribitCommandReceiptJournal(db.Source).RecordAsync(r);
        await new OmsPostgresPrivateEventCapture(db.Source).CaptureAsync(new(Account, r.Order,
            new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, OmsPrivateCaptureSource.WebSocket, "orders")));
        var p = new Ports(DateTimeOffset.UtcNow);
        var result = await p.Gate().AssessWithLedgerAsync(p.Request(), new OmsPostgresRecoveryLedgerReader(db.Source));
        Assert.Equal(1, result.BoundCommands); Assert.Empty(result.CommandIssues);
        Assert.DoesNotContain(DeribitRecoveryEvidenceBlocker.CommandLedgerInvalid, result.Orders.Blockers);
        Assert.Equal(1, p.KnownCalls); Assert.False(result.AccountReady);
    }

    private static OmsRecoveryCommand Row(OmsJournalCommandRequest command,
        OmsRecoveryDispatchPhase phase = OmsRecoveryDispatchPhase.DispatchStarted) => new(command,
        command.ExpectedAggregateVersion.Value + 1, phase,
        phase == OmsRecoveryDispatchPhase.PersistedNotSent ? OmsRecoveryOutboxState.NotSent :
        phase is OmsRecoveryDispatchPhase.Acknowledged or OmsRecoveryDispatchPhase.Rejected
            ? OmsRecoveryOutboxState.Resolved : OmsRecoveryOutboxState.NeedsRecovery,
        phase == OmsRecoveryDispatchPhase.PersistedNotSent ? 0 : 1,
        phase == OmsRecoveryDispatchPhase.PersistedNotSent ? null : phase);

    private sealed class Ports : TimeProvider, IOmsRecoveryLedgerReader, IOmsPrivateRecoveryReader,
        IDeribitRecoveryQueries, IDeribitKnownOrderReader
    {
        private readonly DateTimeOffset now;
        internal readonly DeribitJsonRpcCommandReceipt Receipt;
        internal OmsRecoveryLedgerSnapshot Snapshot;
        internal int QueryCalls, KnownCalls;
        internal Ports(DateTimeOffset? at = null)
        {
            now = at ?? Now; Receipt = DeribitCommandReceiptFixtures.Receipt(Command(), sent: now.AddSeconds(-30));
            Snapshot = new(new(Account, Guid.NewGuid(), now, 1, [new(new(Account, Receipt.Order,
                new(Guid.NewGuid(), Guid.NewGuid(), now.AddSeconds(-20), OmsPrivateCaptureSource.WebSocket, "orders")),
                OmsPrivateCaptureResult.Inserted)], []), [Row(Receipt.Command)], [], [Receipt]);
        }
        public override DateTimeOffset GetUtcNow() => now;
        internal DeribitRecoveryGateRequest Request() => new(new(Account, Epoch, "ETH-PERPETUAL", now.AddHours(-1),
            now.AddSeconds(-10), TimeSpan.FromMinutes(2)), "ETH", OmsPrivateAmountUnit.Usd, "USD");
        internal DeribitRecoveryEvidenceGate Gate() => new(this, this, this, this);
        public Task<OmsRecoveryLedgerSnapshot> ReadAsync(AccountKey scope, int maximumRows = 10000, CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        Task<OmsPrivateRecoveryReadResult> IOmsPrivateRecoveryReader.ReadAsync(AccountKey scope, int maximumRows, CancellationToken cancellationToken) => throw new InvalidOperationException("Independent read forbidden.");
        public Task<DeribitRecoveryQueryResult> CollectAsync(DeribitRecoveryQuery q, CancellationToken ct = default)
        {
            QueryCalls++;
            return Task.FromResult(new DeribitRecoveryQueryResult(Account, Epoch, q.NativeInstrument, "ETH",
                DateTimeOffset.FromUnixTimeMilliseconds((q.FromUtc-q.Overlap).ToUnixTimeMilliseconds()),
                DateTimeOffset.FromUnixTimeMilliseconds(q.ThroughUtc.ToUnixTimeMilliseconds()), now, now,
                new[] { "open", "orders-recent", "orders-historical", "trades-recent", "trades-historical" }
                    .Select(x => new DeribitRecoverySourceRead(x, 1, true)).ToArray(), [], []));
        }
        public Task<DeribitKnownOrderReadResult> ReadAsync(DeribitKnownOrderReadRequest request, CancellationToken ct = default)
        {
            KnownCalls++;
            return Task.FromResult(new DeribitKnownOrderReadResult(Account, Epoch, request.SnapshotId, now, now,
                request.Orders.Count, request.Orders.Select((_, i) => new DeribitKnownOrderLookup(i, DeribitKnownOrderLookupOutcome.Found)).ToArray(),
                request.Orders.Select(f => new OmsPrivateEventEnvelope(Account, f with { VenueTimestampUtc = now },
                    new(Guid.NewGuid(), Epoch, now, OmsPrivateCaptureSource.RecoveryQuery, "private/get_order_state"))).ToArray(), []));
        }
    }
}
