using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

public sealed class DeribitRecoveryRunPostgresTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CurrentAccountInventoryAndCurrencyHistoriesShareActualReplayWindowAndDurableRun(bool unknown)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var r = await DeribitRecoveryReplayGateTests.Rig.Open();
        var sources = new UniversePorts(r, unknown); var q = r.Request();
        var support = new DeribitRecoveryUniverseSources(sources, sources,
            [new("ETH-OTHER", "ETH", "future", OmsPrivateAmountUnit.Usd, "USD", "ETH", "USD")], sources);
        var store = new OmsPostgresRecoveryRunStore(db.Source);
        var engine = new DeribitRecoveryRunEngine(store, new OmsPostgresRecoveryLedgerReader(db.Source), r, r,
            new DeribitRecoveryAccountAssessmentTests.Accounts { Transform = x => x with { Positions = [],
                Balance = x.Balance! with { CashBalance = 0, Equity = 0, CrossCollateralEnabled = false, PortfolioMarginingEnabled = false } } }, universeSources: support);
        var document = await engine.RunAsync(q, DeribitRecoveryAccountAssessmentTests.Request(q), r.Coordinator, r.Session);
        Assert.Equal(unknown ? 1 : 2, sources.InventoryCalls); Assert.Equal(unknown ? 0 : 3, sources.CurrencyCalls);
        Assert.Equal(unknown ? 1 : 5, document.Assessment.Account.Replay!.BufferedObservations);
        Assert.True(document.Assessment.Account.Replay.LocalReplayValidated);
        var saved = (await store.ReadResultAsync(Account, document.Start.RunId))!;
        Assert.NotNull(saved.InventoryObservations); Assert.Equal(!unknown, saved.Universe!.UniverseResolved);
        Assert.Equal(unknown ? 0 : 3, saved.CurrencyObservations!.Count);
        if (unknown) Assert.Contains(saved.Universe.Issues, x => x.Problem == DeribitRecoveryUniverseProblem.UnmappedInstrument);
        else Assert.Equal(new[] { "BTC", "ETH", "USDC" }, saved.CurrencyObservations.Select(x => x.Currency));
        if (!unknown)
        {
            Assert.Equal(DeribitRecoveryCandidateScope.InventoryCurrencies, saved.Candidate!.CoverageScope);
            Assert.Equal(3, saved.Candidate.CurrencyBalances!.Count); Assert.Equal("USD", Assert.Single(saved.Candidate.Positions).SizeCurrency);
            Assert.NotNull(saved.InitialInventoryObservations); Assert.NotNull(saved.VenueClock);
            Assert.All(saved.CurrencyObservations, x => Assert.Equal(saved.VenueClock.VenueTimeUtc, x.ThroughUtc));
        }
        Assert.Contains(DeribitRecoveryEvidenceBlocker.AccountWideCoverageUnproven, saved.Assessment.Ledger.Orders.Blockers);
        Assert.False((await store.ReadHeadAsync(Account))!.CanIncreaseRisk);
    }

    [Fact]
    public async Task BlockedRunPersistsCommandIssuesAndAccountObservationsAcrossSourceRecreation()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync();
        await using var r = await DeribitRecoveryReplayGateTests.Rig.Open();
        var command = OmsRecoveryLedgerPostgresTests.Command(); var journal = new OmsPostgresJournal(db.Source);
        await journal.PersistAsync(command); await journal.MarkDispatchStartedAsync(Account, command.CommandId);
        var ledger = new CountingLedger(new OmsPostgresRecoveryLedgerReader(db.Source));
        var account = new DeribitRecoveryAccountAssessmentTests.Accounts(); var q = r.Request();
        var document = await new DeribitRecoveryRunEngine(new OmsPostgresRecoveryRunStore(db.Source), ledger, r, r, account)
            .RunAsync(q, DeribitRecoveryAccountAssessmentTests.Request(q), r.Coordinator, r.Session);
        Assert.Equal(1, ledger.Calls); Assert.Equal(1, account.Calls); Assert.Equal(0, r.SnapshotCalls);
        Assert.True(document.Assessment.Account.Replay!.LocalReplayValidated);
        Assert.Equal(2, document.Assessment.Ledger.CommandIssues.Count);
        await using var recreated = NpgsqlDataSource.Create(db.ConnectionString);
        var store = new OmsPostgresRecoveryRunStore(recreated);
        var saved = Assert.IsType<DeribitRecoveryRunDocumentV1>(await store.ReadResultAsync(Account, document.Start.RunId));
        Assert.Equal(command.CommandId, Assert.Single(saved.Ledger.Commands).Request.CommandId);
        Assert.Equal(2, saved.Assessment.Ledger.CommandIssues.Count);
        Assert.Equal(-10, Assert.Single(saved.AccountObservations!.Positions).NativeSize);
        Assert.Equal(-0.1m, saved.AccountObservations.Balance!.CashBalance);
        Assert.True(saved.QueryObservations!.TraversalComplete);
        Assert.Empty(saved.Candidate!.Orders.Orders); Assert.Empty(saved.Candidate.Executions);
        Assert.Single(saved.Candidate.Positions); Assert.False(saved.Candidate.Published);
        var head = (await store.ReadHeadAsync(Account))!;
        Assert.Equal(OmsRecoveryRunState.Blocked, head.State); Assert.False(head.CanIncreaseRisk);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.CommandRecoveryUnresolved, saved.Assessment.Account.Evidence.Blockers);
        // Result re-delivery after lost commit ACK preserves exactly the same document and events.
        await store.CompleteBlockedAsync(head.Token, document);
        Assert.Equal(3L, await Scalar(db, "SELECT count(*) FROM oms_recovery_events_v1"));
    }

    [Fact]
    public async Task NewGenerationRejectsLateCompletionAndDisconnectFromOldEpoch()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var store = new OmsPostgresRecoveryRunStore(db.Source);
        var old = await store.BeginAsync(Start()); await store.MarkReconcilingAsync(old);
        var current = await store.BeginAsync(Start());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteBlockedAsync(old, Document(old.Start)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.StopAsync(old, OmsRecoveryStopReason.ConnectionLost));
        Assert.Null(await store.ReadResultAsync(Account, old.Start.RunId));
        var head = (await store.ReadHeadAsync(Account))!;
        Assert.Equal(current, head.Token); Assert.Equal(OmsRecoveryRunState.Recovering, head.State);
        Assert.Equal(old.Generation + 1, current.Generation);
    }

    [Fact]
    public async Task BeginReplayCannotResetExistingRunOrReuseItsIdentityWithNewEpoch()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var store = new OmsPostgresRecoveryRunStore(db.Source);
        var start = Start(); var token = await store.BeginAsync(start); await store.MarkReconcilingAsync(token);
        Assert.Equal(token, await store.BeginAsync(start));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginAsync(start with { Epoch = Guid.NewGuid() }));
        Assert.Equal(OmsRecoveryRunState.Reconciling, (await store.ReadHeadAsync(Account))!.State);
        Assert.Equal(1L, await Scalar(db, "SELECT count(*) FROM oms_recovery_runs_v1"));
        Assert.Equal(2L, await Scalar(db, "SELECT count(*) FROM oms_recovery_events_v1"));
    }

    [Fact]
    public async Task ConcurrentRunStartsHaveUniqueMonotonicGenerations()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var store = new OmsPostgresRecoveryRunStore(db.Source);
        var tokens = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => store.BeginAsync(Start())));
        Assert.Equal(Enumerable.Range(1, 6).Select(x => (long)x), tokens.Select(x => x.Generation).Order());
        Assert.Equal(6, (await store.ReadHeadAsync(Account))!.Token.Generation);
    }

    [Fact]
    public async Task ResultAndHeadAndAlertEventCommitTogetherOrRollBackTogether()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var store = new OmsPostgresRecoveryRunStore(db.Source);
        var token = await store.BeginAsync(Start()); await store.MarkReconcilingAsync(token);
        // Fault exists only in this disposable clone, after result insertion but before transaction commit.
        await Execute(db, """
CREATE FUNCTION synthetic_recovery_commit_failure() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
 IF NEW.reason='CompletedWithIssues' THEN RAISE EXCEPTION 'synthetic event commit failure'; END IF; RETURN NEW; END; $$;
CREATE TRIGGER synthetic_recovery_failure BEFORE INSERT ON oms_recovery_events_v1
 FOR EACH ROW EXECUTE FUNCTION synthetic_recovery_commit_failure();
""");
        await Assert.ThrowsAsync<PostgresException>(() => store.CompleteBlockedAsync(token, Document(token.Start)));
        Assert.Null(await store.ReadResultAsync(Account, token.Start.RunId));
        Assert.Equal(OmsRecoveryRunState.Reconciling, (await store.ReadHeadAsync(Account))!.State);
        Assert.Equal(2L, await Scalar(db, "SELECT count(*) FROM oms_recovery_events_v1"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedOrCancelledCollectionPersistsDisconnectWithoutErasingRun(bool cancel)
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var store = new OmsPostgresRecoveryRunStore(db.Source);
        await using var r = await DeribitRecoveryReplayGateTests.Rig.Open(); using var stop = new CancellationTokenSource();
        r.QueryHook = ct => { if (cancel) { stop.Cancel(); ct.ThrowIfCancellationRequested(); } throw new IOException("synthetic query failure"); };
        var q = r.Request(); var engine = new DeribitRecoveryRunEngine(store, new OmsPostgresRecoveryLedgerReader(db.Source), r, r,
            new DeribitRecoveryAccountAssessmentTests.Accounts());
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RunAsync(q,
            DeribitRecoveryAccountAssessmentTests.Request(q), r.Coordinator, r.Session, ct: stop.Token));
        else await Assert.ThrowsAsync<IOException>(() => engine.RunAsync(q,
            DeribitRecoveryAccountAssessmentTests.Request(q), r.Coordinator, r.Session));
        var head = (await store.ReadHeadAsync(Account))!; Assert.Equal(OmsRecoveryRunState.Disconnected, head.State);
        Assert.Null(await store.ReadResultAsync(Account, head.Token.Start.RunId));
        Assert.Equal(cancel ? "Cancelled" : "Failed", await Scalar(db, "SELECT reason FROM oms_recovery_events_v1 ORDER BY event_id DESC LIMIT 1"));
    }

    [Fact]
    public async Task CaptureGapDuringAccountReadIsSavedAsBlockingEvidence()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); await using var r = await DeribitRecoveryReplayGateTests.Rig.Open();
        var account = new DeribitRecoveryAccountAssessmentTests.Accounts { Hook = async _ => {
            await r.Coordinator.UpdateAsync(r.Session, r.Epoch, OmsPrivateStreamPhase.Recovering, OmsPrivateStreamProblem.TransportLost); } };
        var q = r.Request(); var store = new OmsPostgresRecoveryRunStore(db.Source);
        var document = await new DeribitRecoveryRunEngine(store, new OmsPostgresRecoveryLedgerReader(db.Source), r, r, account)
            .RunAsync(q, DeribitRecoveryAccountAssessmentTests.Request(q), r.Coordinator, r.Session);
        var saved = (await store.ReadResultAsync(Account, document.Start.RunId))!;
        Assert.False(saved.Assessment.Account.Replay!.LocalReplayValidated);
        Assert.Contains(DeribitRecoveryEvidenceBlocker.BufferedReplayInvalid, saved.Assessment.Ledger.Orders.Blockers);
        Assert.False((await store.ReadHeadAsync(Account))!.CanIncreaseRisk);
    }

    [Fact]
    public async Task ScopedReadsAndGenerationBindingPreventCrossAccountMutation()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var store = new OmsPostgresRecoveryRunStore(db.Source);
        var token = await store.BeginAsync(Start()); var foreign = new AccountKey(Account.Tenant, Account.Venue,
            Account.Environment, Account.Account, new("different-subaccount"));
        Assert.Null(await store.ReadHeadAsync(foreign)); Assert.Null(await store.ReadResultAsync(foreign, token.Start.RunId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkReconcilingAsync(token with { Generation = token.Generation + 1 }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkReconcilingAsync(token with { Start = token.Start with { Scope = foreign } }));
        Assert.Equal(OmsRecoveryRunState.Recovering, (await store.ReadHeadAsync(Account))!.State);
    }

    [Fact]
    public async Task ImmutableHistoryAndResultRejectMutationAndConflictingRedelivery()
    {
        await using var db = await OmsPostgresTestDatabase.CreateAsync(); var store = new OmsPostgresRecoveryRunStore(db.Source);
        var token = await store.BeginAsync(Start()); await store.MarkReconcilingAsync(token); var document = Document(token.Start);
        await store.CompleteBlockedAsync(token, document);
        var changed = document with { Assessment = document.Assessment with { Ledger = document.Assessment.Ledger with { JournalCommands = 1 } } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CompleteBlockedAsync(token, changed));
        foreach (var table in new[] { "oms_recovery_runs_v1", "oms_recovery_events_v1", "oms_deribit_recovery_results_v1" })
        {
            await Assert.ThrowsAsync<PostgresException>(() => Execute(db, $"DELETE FROM {table}"));
            await Assert.ThrowsAsync<PostgresException>(() => Execute(db, $"TRUNCATE {table} CASCADE"));
        }
        Assert.NotNull(await store.ReadResultAsync(Account, token.Start.RunId));
    }

    internal static OmsRecoveryRunStart Start() => new(Account, Guid.NewGuid(), Guid.NewGuid(), "ETH-PERPETUAL", "ETH",
        DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddSeconds(-1));
    private static DeribitRecoveryRunDocumentV1 Document(OmsRecoveryRunStart start)
    {
        var snapshot = new OmsPrivateRecoveryReadResult(start.Scope, Guid.NewGuid(), DateTimeOffset.UtcNow, 0, [], []);
        var orders = new DeribitRecoveryGateAssessment(start.Scope, start.Epoch, snapshot.SnapshotId, start.NativeInstrument,
            start.FromUtc, start.ThroughUtc, DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, [DeribitRecoveryEvidenceBlocker.CoherentCutUnavailable]);
        return new(1, start, new(new(orders, null, false, 1, 0, false, [DeribitRecoveryAccountProblem.ReadSkipped]),
            new(orders, 0, 0, [])), new(snapshot, [], []), null);
    }
    private static async Task<object?> Scalar(OmsPostgresTestDatabase db, string sql)
    { await using var cmd = db.Source.CreateCommand(sql); return await cmd.ExecuteScalarAsync(); }
    private static async Task Execute(OmsPostgresTestDatabase db, string sql)
    { await using var cmd = db.Source.CreateCommand(sql); await cmd.ExecuteNonQueryAsync(); }
    private sealed class CountingLedger(IOmsRecoveryLedgerReader inner) : IOmsRecoveryLedgerReader
    {
        internal int Calls;
        public async Task<OmsRecoveryLedgerSnapshot> ReadAsync(AccountKey scope, int maximumRows = 10000, CancellationToken cancellationToken = default)
        { Calls++; return await inner.ReadAsync(scope, maximumRows, cancellationToken); }
    }
    private sealed class UniversePorts(DeribitRecoveryReplayGateTests.Rig rig, bool unknown) : IDeribitAccountInventoryReader, IDeribitCurrencyRecoveryQueries, IDeribitRecoveryClockReader
    {
        internal int InventoryCalls, CurrencyCalls;
        public async Task<DeribitAccountInventoryResult> ReadAsync(DeribitAccountInventoryRequest q, CancellationToken ct = default)
        {
            InventoryCalls++; await rig.Coordinator.TryCaptureReceiptAsync(await rig.Receipt()); var at = DateTimeOffset.UtcNow;
            return new(q.Scope, q.Epoch, at, at, q.MaximumObservationSpan,
                new[] { "ETH", "BTC", "USDC" }.Select(x => new DeribitCurrencyBalance(x, 0, 0, false, false, new(at, at))).ToArray(),
                [new(unknown ? "UNMAPPED" : "ETH-OTHER", "future", "buy", 1, 0.0005m)], [], []);
        }
        public async Task<DeribitCurrencyRecoveryResult> CollectAsync(DeribitCurrencyRecoveryQuery q, CancellationToken ct = default)
        {
            CurrencyCalls++; await rig.Coordinator.TryCaptureReceiptAsync(await rig.Receipt()); var at = DateTimeOffset.UtcNow;
            return new(q.Scope, q.Epoch, q.Currency, q.Kind, DateTimeOffset.FromUnixTimeMilliseconds((q.FromUtc-q.Overlap).ToUnixTimeMilliseconds()),
                DateTimeOffset.FromUnixTimeMilliseconds(q.ThroughUtc.ToUnixTimeMilliseconds()), at, at,
                new[] { "open", "orders-recent", "orders-historical", "trades-recent", "trades-historical" }
                    .Select(x => new DeribitRecoverySourceRead(x, 1, true)).ToArray(), [], []);
        }
        public Task<DeribitRecoveryClockObservation> ReadAsync(DeribitRecoveryClockRequest q, CancellationToken ct = default)
        {
            var at = DateTimeOffset.UtcNow;
            return Task.FromResult(new DeribitRecoveryClockObservation(q.Scope, q.Epoch, at, at,
                DateTimeOffset.FromUnixTimeMilliseconds(at.AddMilliseconds(-100).ToUnixTimeMilliseconds())));
        }
    }
}
