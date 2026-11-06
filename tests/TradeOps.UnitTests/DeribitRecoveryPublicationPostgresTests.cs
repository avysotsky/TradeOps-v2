using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;

public sealed class DeribitRecoveryPublicationPostgresTests
{
    [Fact]
    public async Task CorruptCommittedRecoveryArchiveCannotBecomeAUsableLedgerOrNewPublishedState()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var live = await Live.Open(db);
        var first = await live.Recover(true); Assert.NotNull(first.RuntimeGate);
        await using (var corrupt = db.Source.CreateCommand("""
ALTER TABLE oms_deribit_recovery_results_v1 DISABLE TRIGGER tr_recovery_results_v1_immutable;
UPDATE oms_deribit_recovery_results_v1 SET document_sha256=decode(repeat('00',32),'hex') WHERE run_id=@run;
ALTER TABLE oms_deribit_recovery_results_v1 ENABLE TRIGGER tr_recovery_results_v1_immutable;
"""))
        { corrupt.Parameters.AddWithValue("run", first.Document.Start.RunId); await corrupt.ExecuteNonQueryAsync(); }
        var snapshot = await new OmsPostgresRecoveryLedgerReader(db.Source, true).ReadAsync(Scope());
        Assert.False(snapshot.IsUsable); Assert.Null(snapshot.RecoveryArchive);
        Assert.Contains(OmsRecoveryLedgerProblem.InvalidRecord, snapshot.Problems);
        var next = await live.Recover(true); Assert.Null(next.RuntimeGate);
        Assert.Contains(DeribitRecoveryCoherenceProblem.JournalUnavailable, next.Coherence.Problems);
        Assert.False(await first.RuntimeGate.CanUseRecoveredStateAsync());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task QueryRecoveredFillsSurviveRestartBeyondOverlapAndDeduplicateLaterWebSocketDelivery(bool duplicateWebSocket)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var clock = new OffsetClock();
        OmsRecoveredAccountArchive first; var command = DeribitCommandReceiptFixtures.Command();
        await new OmsPostgresJournal(db.Source).PersistAsync(command);
        await new OmsPostgresJournal(db.Source).MarkDispatchStartedAsync(Scope(), command.CommandId);
        var receipt = DeribitCommandReceiptFixtures.Receipt(command, sent: DateTimeOffset.UtcNow);
        await new OmsPostgresDeribitCommandReceiptJournal(db.Source).RecordAsync(receipt);
        await using (var initial = await Live.Open(db, clock))
        {
            clock.Offset = TimeSpan.FromMinutes(20);
            initial.FromOverride = clock.GetUtcNow().AddMinutes(-22);
            initial.NativeReport = receipt.Order with { OrderState = "filled", FilledNativeAmount = 1 };
            initial.FullFill = true; initial.DuplicateFill = true;
            var outcome = await initial.Recover(true);
            Assert.True(outcome.Coherence.EvidenceSufficient, string.Join(",", outcome.Coherence.Problems));
            first = (await initial.Store.ReadPublishedAsync(Scope()))!;
            Assert.Single(first.State.Executions);
            Assert.Empty((await new OmsPostgresPrivateRecoveryReader(db.Source).ReadAsync(Scope())).Observations);
        }
        if (duplicateWebSocket)
        {
            var writer = new OmsPostgresPrivateEventCapture(db.Source); var epoch = Guid.NewGuid();
            foreach (var fact in new[] { first.State.Orders.Single().LastReport, first.State.Executions.Single() })
                await writer.CaptureAsync(new(Scope(), fact, new(Guid.NewGuid(), epoch, DateTimeOffset.UtcNow,
                    OmsPrivateCaptureSource.WebSocket, fact.Kind == OmsJournalEventKind.Order ? "user.orders.any.any.raw" : "user.trades.any.any.100ms")));
        }
        clock.Offset = TimeSpan.FromMinutes(32);
        await using var restarted = await Live.Open(db, clock); restarted.FromOverride = first.State.NativeThroughUtc;
        var second = await restarted.Recover(true);
        Assert.True(second.Coherence.EvidenceSufficient, string.Join(",", second.Coherence.Problems));
        Assert.All(second.Document.CurrencyObservations!, q => Assert.Empty(q.Observations));
        Assert.NotEqual(first.State.Epoch, second.Document.Start.Epoch);
        var archive = (await restarted.Store.ReadPublishedAsync(Scope()))!;
        Assert.Equal(first.State.Executions.Single(), Assert.Single(archive.State.Executions));
        Assert.Equal("filled", Assert.Single(archive.State.Orders).State.NativeState);
        Assert.Equal(1, Assert.Single(archive.State.Orders).State.KnownFilledNativeAmount);
        Assert.NotNull(second.Document.Ledger.RecoveryArchive);
        clock.Offset = TimeSpan.FromMinutes(33); restarted.FromOverride = archive.State.NativeThroughUtc;
        var third = await restarted.Recover(true);
        Assert.True(third.Coherence.EvidenceSufficient, string.Join(",", third.Coherence.Problems));
        Assert.Equal(first.State.Executions.Single(), Assert.Single((await restarted.Store.ReadPublishedAsync(Scope()))!.State.Executions));
    }

    [Fact]
    public async Task ExternalOrderAdoptionUsesActualAuthorizedRoleAndWithdrawImmediatelyInvalidatesBaseline()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var live = await Live.Open(db);
        live.NativeReport = DeribitCommandReceiptFixtures.Receipt(DeribitCommandReceiptFixtures.Command(), sent: DateTimeOffset.UtcNow).Order;
        var blocked = await live.Recover(true); Assert.Null(blocked.RuntimeGate);
        await using var role = await ReviewRole.Open(db);
        var request = new OmsRecoveryExternalOrderReviewRequest(Scope(), Guid.NewGuid(), blocked.Document.Start.RunId,
            "ETH", live.NativeReport.ExchangeOrderId, OmsRecoveryExternalOrderDecision.AcceptObservedExternalOrder, "Operator reviewed observed external order", 0);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => role.Store.ReviewAsync(request)); Assert.Equal("42501", denied.SqlState);
        await role.Allow(); var accepted = await role.Store.ReviewAsync(request);
        Assert.Equal(role.Name, accepted.ActorRole); Assert.Equal(1, accepted.EvidenceVersion);
        Assert.Equal(accepted, await role.Store.ReviewAsync(request));
        await Assert.ThrowsAsync<PostgresException>(() => role.Store.ReviewAsync(request with { Reason = "identity collision" }));
        var recovered = await live.Recover(true);
        Assert.True(recovered.Coherence.EvidenceSufficient, string.Join(",", recovered.Coherence.Problems));
        Assert.True(await recovered.RuntimeGate!.CanUseRecoveredStateAsync());
        var archive = (await live.Store.ReadPublishedAsync(Scope()))!;
        Assert.Null(Assert.Single(archive.State.Orders).LocalOrderId); // explicit external adoption creates no command/receipt
        var withdrawn = await role.Store.ReviewAsync(request with { DecisionId = Guid.NewGuid(), SourceRunId = recovered.Document.Start.RunId,
            Decision = OmsRecoveryExternalOrderDecision.WithdrawAcceptance, ExpectedEvidenceVersion = archive.Publication.EvidenceVersion });
        Assert.True(withdrawn.EvidenceVersion > accepted.EvidenceVersion);
        Assert.False(await recovered.RuntimeGate.CanUseRecoveredStateAsync());
        Assert.Null((await live.Recover(true)).RuntimeGate);
        await using var immutable = db.Source.CreateCommand("DELETE FROM oms_recovery_external_reviews_v1");
        await Assert.ThrowsAsync<PostgresException>(() => immutable.ExecuteNonQueryAsync());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OperatorDecisionCannotApproveDifferentNativeTermsOrResolveUnprovenCommand(bool unresolvedCommand)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var live = await Live.Open(db);
        var command = DeribitCommandReceiptFixtures.Command();
        if (unresolvedCommand)
        {
            var journal = new OmsPostgresJournal(db.Source); await journal.PersistAsync(command); await journal.MarkDispatchStartedAsync(Scope(), command.CommandId);
        }
        live.NativeReport = DeribitCommandReceiptFixtures.Receipt(command, sent: DateTimeOffset.UtcNow).Order;
        var blocked = await live.Recover(true); Assert.Null(blocked.RuntimeGate);
        await using var role = await ReviewRole.Open(db); await role.Allow();
        var version = (await new OmsPostgresRecoveryLedgerReader(db.Source, true).ReadAsync(Scope())).EvidenceVersion!.Value;
        await role.Store.ReviewAsync(new(Scope(), Guid.NewGuid(), blocked.Document.Start.RunId, "ETH", live.NativeReport.ExchangeOrderId,
            OmsRecoveryExternalOrderDecision.AcceptObservedExternalOrder, "Explicit external-order review", version));
        if (!unresolvedCommand) live.NativeReport = live.NativeReport with { Price = live.NativeReport.Price + 1 };
        var result = await live.Recover(true); Assert.Null(result.RuntimeGate);
        Assert.Contains(unresolvedCommand ? DeribitRecoveryCoherenceProblem.UnresolvedCommand : DeribitRecoveryCoherenceProblem.UnknownExternalOrder, result.Coherence.Problems);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AmbiguousPlaceAndCancelWithDirectReceiptsResolveAtomicallyWithoutResending(bool failCommit)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var live = await Live.Open(db);
        var journal = new OmsPostgresJournal(db.Source); var receipts = new OmsPostgresDeribitCommandReceiptJournal(db.Source);
        var place = DeribitCommandReceiptFixtures.Command(); await journal.PersistAsync(place);
        await journal.MarkDispatchStartedAsync(Scope(), place.CommandId);
        var placed = DeribitCommandReceiptFixtures.Receipt(place, sent: DateTimeOffset.UtcNow);
        Assert.Equal(DeribitCommandReceiptWriteResult.Inserted, await receipts.RecordAsync(placed));
        await journal.RecordOutcomeAsync(Scope(), place.CommandId, OmsJournalDispatchOutcome.Ambiguous);
        var cancel = DeribitCommandReceiptFixtures.Cancel(place); await journal.PersistAsync(cancel);
        await journal.MarkDispatchStartedAsync(Scope(), cancel.CommandId);
        var cancelled = DeribitCommandReceiptFixtures.Receipt(cancel, rpcId: 2, sent: DateTimeOffset.UtcNow);
        Assert.Equal(DeribitCommandReceiptWriteResult.Inserted, await receipts.RecordAsync(cancelled));
        live.NativeReport = cancelled.Order;
        if (failCommit)
        {
            await using var fault = db.Source.CreateCommand("""
CREATE FUNCTION synthetic_command_resolution_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
 IF NEW.reason='QualifiedPublication' THEN RAISE EXCEPTION 'synthetic resolution commit failure'; END IF; RETURN NEW; END; $$;
CREATE TRIGGER synthetic_command_resolution_failure BEFORE INSERT ON oms_recovery_events_v1
 FOR EACH ROW EXECUTE FUNCTION synthetic_command_resolution_failure();
""");
            await fault.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<PostgresException>(() => live.Recover(true));
            var failed = await new OmsPostgresRecoveryLedgerReader(db.Source, true).ReadAsync(Scope());
            Assert.Equal(OmsRecoveryDispatchPhase.Ambiguous, failed.Commands.Single(x => x.Request.CommandId == place.CommandId).Phase);
            Assert.Equal(OmsRecoveryDispatchPhase.DispatchStarted, failed.Commands.Single(x => x.Request.CommandId == cancel.CommandId).Phase);
            Assert.All(failed.Commands, c => Assert.Equal(OmsRecoveryOutboxState.NeedsRecovery, c.Outbox));
            Assert.Null(await live.Store.ReadPublishedAsync(Scope())); return;
        }
        var result = await live.Recover(publish: true);
        Assert.True(result.Coherence.EvidenceSufficient, string.Join(",", result.Coherence.Problems));
        var snapshot = await new OmsPostgresRecoveryLedgerReader(db.Source, true).ReadAsync(Scope());
        Assert.All(snapshot.Commands, c => { Assert.Equal(OmsRecoveryDispatchPhase.Acknowledged, c.Phase); Assert.Equal(OmsRecoveryOutboxState.Resolved, c.Outbox); Assert.Equal(1, c.AttemptCount); });
        var archive = (await live.Store.ReadPublishedAsync(Scope()))!;
        Assert.Equal(place.LocalOrderId, Assert.Single(archive.State.Orders).LocalOrderId);
        Assert.Equal("cancelled", Assert.Single(archive.State.Orders).State.NativeState);
        Assert.Equal(snapshot.EvidenceVersion, archive.Publication.EvidenceVersion);
        Assert.True(await result.RuntimeGate!.CanUseRecoveredStateAsync());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MatchingLabelCannotResolveLostReplyAndLateExecutionCannotReopenTerminal(bool lateExecution)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var live = await Live.Open(db);
        var journal = new OmsPostgresJournal(db.Source); var command = DeribitCommandReceiptFixtures.Command();
        await journal.PersistAsync(command); await journal.MarkDispatchStartedAsync(Scope(), command.CommandId);
        var receipt = DeribitCommandReceiptFixtures.Receipt(command, sent: DateTimeOffset.UtcNow);
        if (lateExecution) await new OmsPostgresDeribitCommandReceiptJournal(db.Source).RecordAsync(receipt);
        live.NativeReport = receipt.Order with { OrderState = "cancelled", FilledNativeAmount = 0 };
        if (lateExecution) live.LateFill = true;
        var result = await live.Recover(publish: true); Assert.False(result.Coherence.EvidenceSufficient);
        Assert.Null(await live.Store.ReadPublishedAsync(Scope()));
        Assert.Equal(OmsRecoveryDispatchPhase.DispatchStarted,
            Assert.Single((await new OmsPostgresRecoveryLedgerReader(db.Source, true).ReadAsync(Scope())).Commands).Phase);
    }

    [Fact]
    public async Task ActualGlobalStreamPublishesQuietWholeAccountAndRestartCannotRestoreAuthority()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var live = await Live.Open(db);
        var result = await live.Recover(publish: true);
        Assert.True(result.Coherence.EvidenceSufficient, string.Join(",", result.Coherence.Problems));
        Assert.NotNull(result.RuntimeGate); Assert.True(await result.RuntimeGate.CanUseRecoveredStateAsync());
        var archive = (await live.Store.ReadPublishedAsync(Scope()))!;
        Assert.Equal(2, archive.State.Balances.Count); Assert.Empty(archive.State.Orders);
        Assert.Equal(OmsRecoveryRunState.Ready, (await live.Store.ReadHeadAsync(Scope()))!.State);
        Assert.False((await live.Store.ReadHeadAsync(Scope()))!.CanIncreaseRisk);
        var saved = (await live.Store.ReadResultAsync(Scope(), result.Document.Start.RunId))!;
        Assert.Null(DeribitRecoveryCoherencePolicy.Assess(saved).Publication);
        await live.StopStream(); Assert.False(await result.RuntimeGate.CanUseRecoveredStateAsync());
        Assert.NotNull(await live.Store.ReadPublishedAsync(Scope())); // archive survives; live authority does not
    }

    [Theory]
    [InlineData("cash")] [InlineData("missing-currency")] [InlineData("long-outage")]
    [InlineData("clock")] [InlineData("unmapped")] [InlineData("pre-window")]
    public async Task IncompleteOrChangedInputsStayBlockedWithoutPublishing(string mode)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var live = await Live.Open(db);
        live.Mode = mode; var result = await live.Recover(publish: true);
        Assert.False(result.Coherence.EvidenceSufficient); Assert.Null(result.RuntimeGate);
        Assert.Null(await live.Store.ReadPublishedAsync(Scope()));
        Assert.Equal(OmsRecoveryRunState.Blocked, (await live.Store.ReadHeadAsync(Scope()))!.State);
    }

    [Fact]
    public async Task NextRecoveryCannotSkipIntervalAndFailureRetainsLastArchiveWithoutRuntimeAuthority()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var live = await Live.Open(db);
        var initial = await live.Recover(true); Assert.NotNull(initial.RuntimeGate);
        var before = (await live.Store.ReadPublishedAsync(Scope()))!;
        live.FromOverride = before.State.NativeThroughUtc.AddMilliseconds(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => live.Recover(true));
        Assert.Equal(before.Publication, (await live.Store.ReadPublishedAsync(Scope()))!.Publication);
        Assert.False(await initial.RuntimeGate.CanUseRecoveredStateAsync());
        Assert.Equal(OmsRecoveryRunState.Disconnected, (await live.Store.ReadHeadAsync(Scope()))!.State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FilledOrderUsesExactlyOneNativeExecutionDespiteRepeatedHistoryObservation(bool duplicate)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var live = await Live.Open(db);
        var command = DeribitCommandReceiptFixtures.Command(); var journal = new OmsPostgresJournal(db.Source);
        await journal.PersistAsync(command); await journal.MarkDispatchStartedAsync(Scope(), command.CommandId);
        var receipt = DeribitCommandReceiptFixtures.Receipt(command, sent: DateTimeOffset.UtcNow);
        await new OmsPostgresDeribitCommandReceiptJournal(db.Source).RecordAsync(receipt);
        live.NativeReport = receipt.Order with { OrderState = "filled", FilledNativeAmount = 1 };
        live.FullFill = true; live.DuplicateFill = duplicate;
        var result = await live.Recover(true);
        Assert.True(result.Coherence.EvidenceSufficient, string.Join(",", result.Coherence.Problems));
        var state = (await live.Store.ReadPublishedAsync(Scope()))!.State;
        Assert.Equal(1, Assert.Single(state.Executions).NativeAmount);
        Assert.Equal(1, Assert.Single(state.Orders).State.KnownFilledNativeAmount);
    }

    [Fact]
    public async Task JournalMutationAfterAssessmentRejectsPublicationWithoutPartialState()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var live = await Live.Open(db);
        var (token, doc, qualified) = await live.Prepare();
        await new OmsPostgresJournal(db.Source).PersistAsync(OmsRecoveryLedgerPostgresTests.Command());
        await Assert.ThrowsAsync<InvalidOperationException>(() => live.Store.PublishAsync(token, doc, qualified));
        Assert.Null(await live.Store.ReadPublishedAsync(Scope())); Assert.Null(await live.Store.ReadResultAsync(Scope(), token.Start.RunId));
        Assert.Equal(OmsRecoveryRunState.Reconciling, (await live.Store.ReadHeadAsync(Scope()))!.State);
    }

    [Fact]
    public async Task PublicationRollbackPreservesNoProjectionOrReadyAndExactRetryIsIdempotent()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var live = await Live.Open(db);
        await using (var fault = db.Source.CreateCommand("""
CREATE FUNCTION synthetic_publication_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
 IF NEW.reason='QualifiedPublication' THEN RAISE EXCEPTION 'synthetic publication failure'; END IF; RETURN NEW; END; $$;
CREATE TRIGGER synthetic_publication_failure BEFORE INSERT ON oms_recovery_events_v1
 FOR EACH ROW EXECUTE FUNCTION synthetic_publication_failure();
""")) await fault.ExecuteNonQueryAsync();
        var (token, doc, qualified) = await live.Prepare();
        await Assert.ThrowsAsync<PostgresException>(() => live.Store.PublishAsync(token, doc, qualified));
        Assert.Null(await live.Store.ReadPublishedAsync(Scope())); Assert.Null(await live.Store.ReadResultAsync(Scope(), token.Start.RunId));
        Assert.Equal(OmsRecoveryRunState.Reconciling, (await live.Store.ReadHeadAsync(Scope()))!.State);
        await using (var clear = db.Source.CreateCommand("DROP TRIGGER synthetic_publication_failure ON oms_recovery_events_v1")) await clear.ExecuteNonQueryAsync();
        var published = await live.Store.PublishAsync(token, doc, qualified);
        Assert.Equal(published, await live.Store.PublishAsync(token, doc, qualified));
        await using var count = db.Source.CreateCommand("SELECT count(*) FROM oms_recovered_account_states_v1");
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("journal")] [InlineData("ingress")] [InlineData("new-run")]
    public async Task RuntimeGateRejectsChangesAfterCommit(string change)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var live = await Live.Open(db);
        var result = await live.Recover(publish: true); Assert.NotNull(result.RuntimeGate);
        Assert.True(await result.RuntimeGate.CanUseRecoveredStateAsync());
        if (change == "journal") await new OmsPostgresJournal(db.Source).PersistAsync(OmsRecoveryLedgerPostgresTests.Command());
        else if (change == "ingress")
            await live.Coordinator.RegisterReceiptAsync(live.Session, result.Document.Start.Epoch,
                OmsPrivateCaptureReplayTests.Event(result.Document.Start.Epoch)); // intentionally not committed yet
        else await live.Store.BeginAsync(result.Document.Start with { RunId = Guid.NewGuid() });
        Assert.False(await result.RuntimeGate.CanUseRecoveredStateAsync());
    }

    private sealed class OffsetClock : TimeProvider
    {
        internal TimeSpan Offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
    }

    private sealed class Live(OmsPostgresTestDatabase db, TimeProvider? clock = null) : IAsyncDisposable, IDeribitAccountInventoryReader,
        IDeribitCurrencyRecoveryQueries, IDeribitRecoveryClockReader, IDeribitRecoveryQueries, IDeribitKnownOrderReader
    {
        private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
        internal readonly OmsPrivateCaptureCoordinator Coordinator = new(new OmsPostgresPrivateEventCapture(db.Source), new(), clock);
        internal readonly OmsPostgresRecoveryRunStore Store = new(db.Source);
        internal OmsPrivateCaptureSession Session = null!;
        private readonly CancellationTokenSource stop = new(); private Task capture = null!;
        private DeribitRecoveryReplayGateTests.Rig queries = null!;
        internal string Mode = ""; private int inventoryCalls;
        internal OmsPrivateVenueFacts? NativeReport;
        internal bool LateFill, FullFill, DuplicateFill;
        internal DateTimeOffset? FromOverride;
        internal static async Task<Live> Open(OmsPostgresTestDatabase db, TimeProvider? clock = null)
        {
            var live = new Live(db, clock); live.queries = await DeribitRecoveryReplayGateTests.Rig.Open();
            live.Session = await live.Coordinator.SwitchAsync(Scope()); var socket = new Socket();
            live.capture = new DeribitPrivateWebSocketStream(Options(), NullLogger<DeribitPrivateWebSocketStream>.Instance, () => socket, clock).RunCaptureAsync(live.Coordinator, live.Session,
                DeribitPrivateCapturePlan.ForAccountRecovery(123, false, Plan().Instruments.Values, Plan().Limits), live.stop.Token);
            using var deadline = new CancellationTokenSource(Deadline);
            while (live.Coordinator.Status?.Phase != OmsPrivateStreamPhase.Recovering) await Task.Delay(5, deadline.Token);
            return live;
        }
        internal async Task<DeribitRecoveryRunOutcome> Recover(bool publish)
        {
            var q = queries.Request(); q = q with { Query = q.Query with { Epoch = Coordinator.Status!.Epoch,
                FromUtc = FromOverride ?? Now.AddMinutes(Mode == "long-outage" ? -40 : -5),
                ThroughUtc = FromOverride ?? Now.AddSeconds(-1) } };
            var account = new DeribitRecoveryAccountAssessmentTests.Accounts(clock) { Transform = x => x with { Positions = [],
                Balance = x.Balance! with { CashBalance = 0, Equity = 0, CrossCollateralEnabled = false, PortfolioMarginingEnabled = false } } };
            IOmsRecoveryLedgerReader journal = new OmsPostgresRecoveryLedgerReader(db.Source, true);
            if (clock is not null) journal = new ClockedLedger(journal, clock);
            var engine = new DeribitRecoveryRunEngine(Store, journal, this, this,
                account, clock, universeSources: new(this, this,
                    [new("ETH-PERPETUAL", "ETH", "future", OmsPrivateAmountUnit.Usd, "USD", "ETH", "USD")], this));
            if (publish) return await engine.RecoverAndPublishAsync(q, DeribitRecoveryAccountAssessmentTests.Request(q), Coordinator, Session);
            var doc = await engine.RunAsync(q, DeribitRecoveryAccountAssessmentTests.Request(q), Coordinator, Session);
            return new(doc, DeribitRecoveryCoherencePolicy.Assess(doc, clock).Assessment, null);
        }
        internal async Task<(OmsRecoveryRunToken, DeribitRecoveryRunDocumentV1, OmsQualifiedRecovery)> Prepare()
        {
            var result = await Recover(false); var doc = result.Document with { Start = result.Document.Start with { RunId = Guid.NewGuid() } };
            var token = await Store.BeginAsync(doc.Start); await Store.MarkReconcilingAsync(token);
            var assessed = DeribitRecoveryCoherencePolicy.Assess(doc, clock);
            Assert.True(assessed.Assessment.EvidenceSufficient, string.Join(",", assessed.Assessment.Problems));
            return (token, assessed.Document, assessed.Publication!);
        }
        public Task<DeribitAccountInventoryResult> ReadAsync(DeribitAccountInventoryRequest q, CancellationToken ct = default)
        {
            var at = Now; inventoryCalls++; if (Mode == "pre-window") at = at.AddSeconds(-1);
            return Task.FromResult(new DeribitAccountInventoryResult(q.Scope, q.Epoch, at, at, q.MaximumObservationSpan,
                new[] { "ETH", "BTC" }.Select(x => new DeribitCurrencyBalance(x, Mode == "cash" && inventoryCalls > 1 ? 1 : 0,
                    0, false, false, new(at, at))).ToArray(),
                Mode == "unmapped" ? [new("UNKNOWN", "future", "buy", 1)] : [],
                NativeReport is { OrderState: "open" } open ? [new(open.NativeInstrument, open.ExchangeOrderId, "open", "limit", new string('A', 64))] : [], []));
        }
        public Task<DeribitCurrencyRecoveryResult> CollectAsync(DeribitCurrencyRecoveryQuery q, CancellationToken ct = default)
        {
            var at = Now;
            return Task.FromResult(new DeribitCurrencyRecoveryResult(q.Scope, q.Epoch, q.Currency, q.Kind,
                DateTimeOffset.FromUnixTimeMilliseconds((q.FromUtc-q.Overlap).ToUnixTimeMilliseconds()), q.ThroughUtc, at, at,
                new[] { "open", "orders-recent", "orders-historical", "trades-recent", "trades-historical" }
                    .Select(x => new DeribitRecoverySourceRead(x, 1, true)).ToArray(), Facts(q, at),
                Mode == "missing-currency" && q.Currency == "BTC" ? [DeribitCurrencyRecoveryProblem.ReadFailed] : []));
        }
        private OmsPrivateEventEnvelope[] Facts(DeribitCurrencyRecoveryQuery q, DateTimeOffset at)
        {
            if (NativeReport is not { } report || q.Currency != report.Currency) return [];
            var observation = new OmsPrivateObservation(Guid.NewGuid(), q.Epoch, at,
                OmsPrivateCaptureSource.RecoveryQuery, "private/get_order_history_by_currency");
            var order = new OmsPrivateEventEnvelope(q.Scope, report, observation);
            if (!LateFill && !FullFill) return [order];
            var fill = report with { Kind = OmsJournalEventKind.Execution, NativeAmount = FullFill ? 1 : 0.5m,
                VenueTimestampUtc = report.VenueTimestampUtc.AddMilliseconds(LateFill ? 1 : 0), OrderState = null,
                FilledNativeAmount = null, ExchangeTradeId = "late-fill", Fee = 0, FeeCurrency = "ETH" };
            var execution = new OmsPrivateEventEnvelope(q.Scope, fill, observation with { CaptureId = Guid.NewGuid(), Channel = "private/get_user_trades_by_currency" });
            return DuplicateFill ? [order, execution, execution with { Observation = execution.Observation with { CaptureId = Guid.NewGuid() } }] : [order, execution];
        }
        public Task<DeribitRecoveryClockObservation> ReadAsync(DeribitRecoveryClockRequest q, CancellationToken ct = default)
        {
            var at = Now;
            return Task.FromResult(new DeribitRecoveryClockObservation(q.Scope, q.Epoch, at, at,
                Mode == "clock" ? null : DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds())));
        }
        internal async Task StopStream() { stop.Cancel(); await capture.WaitAsync(Deadline); }
        public Task<DeribitRecoveryQueryResult> CollectAsync(DeribitRecoveryQuery q, CancellationToken ct = default)
        {
            var at = Now;
            return Task.FromResult(new DeribitRecoveryQueryResult(q.Scope, q.Epoch, q.NativeInstrument, "ETH",
                DateTimeOffset.FromUnixTimeMilliseconds((q.FromUtc-q.Overlap).ToUnixTimeMilliseconds()),
                DateTimeOffset.FromUnixTimeMilliseconds(q.ThroughUtc.ToUnixTimeMilliseconds()), at, at,
                new[] { "open", "orders-recent", "orders-historical", "trades-recent", "trades-historical" }.Select(x => new DeribitRecoverySourceRead(x, 1, true)).ToArray(), [], []));
        }
        public Task<DeribitKnownOrderReadResult> ReadAsync(DeribitKnownOrderReadRequest q, CancellationToken ct = default) =>
            throw new InvalidOperationException("Retained terminal evidence must not invent an addressed lookup.");
        public async ValueTask DisposeAsync() { await StopStream(); stop.Dispose(); await Coordinator.DisposeAsync(); await queries.DisposeAsync(); }
    }

    // The SQL facts/version still come from an actual RR transaction; only its wall-clock
    // metadata follows the controlled application clock so the test need not sleep for 20 minutes.
    private sealed class ClockedLedger(IOmsRecoveryLedgerReader inner, TimeProvider clock) : IOmsRecoveryLedgerReader
    {
        public async Task<OmsRecoveryLedgerSnapshot> ReadAsync(AccountKey scope, int maximumRows = 10000, CancellationToken cancellationToken = default)
        {
            var snapshot = await inner.ReadAsync(scope, maximumRows, cancellationToken);
            return snapshot with { Private = snapshot.Private with { ReadAtUtc = clock.GetUtcNow() } };
        }
    }

    private sealed class ReviewRole(OmsPostgresTestDatabase db) : IAsyncDisposable
    {
        internal readonly string Name = "to_review_" + Guid.NewGuid().ToString("N");
        private NpgsqlDataSource source = null!;
        internal OmsPostgresRecoveryRunStore Store = null!;
        internal static async Task<ReviewRole> Open(OmsPostgresTestDatabase db)
        {
            var r = new ReviewRole(db);
            await using (var role = db.Source.CreateCommand($"CREATE ROLE \"{r.Name}\" NOLOGIN; GRANT USAGE ON SCHEMA public TO \"{r.Name}\"; GRANT SELECT ON oms_deribit_recovery_results_v1 TO \"{r.Name}\"; GRANT EXECUTE ON FUNCTION public.oms_append_recovery_external_review_v1(text,text,smallint,text,text,uuid,uuid,text,text,bytea,smallint,text,bigint) TO \"{r.Name}\""))
                await role.ExecuteNonQueryAsync();
            var settings = new NpgsqlConnectionStringBuilder(db.ConnectionString) { Pooling = false, Options = "-c role=" + r.Name };
            r.source = NpgsqlDataSource.Create(settings.ConnectionString); r.Store = new(r.source); return r;
        }
        internal async Task Allow()
        {
            await using var grant = db.Source.CreateCommand("INSERT INTO oms_recovery_review_permissions_v1 VALUES ('tenant','DERIBIT',@environment,'account','',@role)");
            grant.Parameters.AddWithValue("environment", (short)Scope().Environment);
            grant.Parameters.AddWithValue("role", Name); await grant.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await source.DisposeAsync();
            await using var cleanup = db.Source.CreateCommand($"REVOKE SELECT ON oms_deribit_recovery_results_v1 FROM \"{Name}\"; REVOKE USAGE ON SCHEMA public FROM \"{Name}\"; REVOKE EXECUTE ON FUNCTION public.oms_append_recovery_external_review_v1(text,text,smallint,text,text,uuid,uuid,text,text,bytea,smallint,text,bigint) FROM \"{Name}\"; DROP ROLE \"{Name}\"");
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
