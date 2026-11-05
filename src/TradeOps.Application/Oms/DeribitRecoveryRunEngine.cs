namespace TradeOps.Application.Oms;

public sealed record DeribitRecoveryUniverseSources(IDeribitAccountInventoryReader Inventory,
    IDeribitCurrencyRecoveryQueries CurrencyQueries, IReadOnlyList<DeribitRecoveryInstrument> TrustedManifest);

/// <summary>Durable lifecycle around the actual ledger/account/replay gate. No dispatch, caller-provided
/// completeness flags, swallowed errors or READY shortcut. Existing read-only hosts remain read-only.</summary>
public sealed class DeribitRecoveryRunEngine(IOmsRecoveryRunStore store, IOmsRecoveryLedgerReader ledger,
    IDeribitRecoveryQueries queries, IDeribitKnownOrderReader known, IDeribitAccountSnapshotReader account,
    TimeProvider? clock = null, DeribitRecoveryUniverseSources? universeSources = null)
{
    public async Task<DeribitRecoveryRunDocumentV1> RunAsync(DeribitRecoveryGateRequest request,
        DeribitRecoveryAccountRequest accountRequest, OmsPrivateCaptureCoordinator coordinator,
        OmsPrivateCaptureSession session, DeribitReplayDrainOptions? drainOptions = null, CancellationToken ct = default)
    {
        var q = request.Query;
        var start = new OmsRecoveryRunStart(q.Scope, Guid.NewGuid(), q.Epoch, q.NativeInstrument,
            request.Currency, q.FromUtc, q.ThroughUtc);
        start.Validate(); ct.ThrowIfCancellationRequested();
        var token = await store.BeginAsync(start, ct);
        try
        {
            await store.MarkReconcilingAsync(token, ct);
            var recordedLedger = new RecordedLedger(ledger);
            var universeRead = universeSources is null ? null : new UniverseRead(universeSources, start, recordedLedger,
                accountRequest.Query.MaximumObservationSpan, clock ?? TimeProvider.System);
            var recordedAccount = new RecordedAccount(account, universeRead is null ? null : universeRead.ReadAsync);
            var recordedQueries = new RecordedQueries(queries);
            var recordedKnown = new RecordedKnown(known);
            var gate = new DeribitRecoveryEvidenceGate(new NoIndependentPrivateRead(), recordedQueries, recordedKnown, clock);
            var result = await gate.AssessAccountWithLedgerAndReplayAsync(request, recordedLedger, accountRequest,
                recordedAccount, coordinator, session, drainOptions, ct);
            var document = new DeribitRecoveryRunDocumentV1(1, start, result,
                recordedLedger.Snapshot ?? throw new InvalidOperationException("Missing run journal snapshot."), recordedAccount.Observed,
                recordedQueries.Observed, recordedKnown.Observed, InventoryObservations: universeRead?.Inventory,
                Universe: universeRead?.Finish(), CurrencyObservations: universeRead?.CurrencyReads.ToArray());
            document = document with { Candidate = DeribitRecoveryCandidateBuilder.Build(document) };
            document.Validate();
            await store.CompleteBlockedAsync(token, document, ct);
            return document;
        }
        catch (Exception error)
        {
            // A cancelled caller must not erase the durable reason. Failure to record it is surfaced,
            // not mistaken for successful cleanup; persisted Recovering/Reconciling also denies readiness.
            using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await store.StopAsync(token, error is OperationCanceledException ? OmsRecoveryStopReason.Cancelled : OmsRecoveryStopReason.Failed, finish.Token); }
            catch (Exception stopError) { throw new AggregateException("Recovery failed; its final durable state must be inspected.", error, stopError); }
            throw;
        }
    }

    private sealed class RecordedLedger(IOmsRecoveryLedgerReader inner) : IOmsRecoveryLedgerReader
    {
        internal OmsRecoveryLedgerSnapshot? Snapshot;
        public async Task<OmsRecoveryLedgerSnapshot> ReadAsync(TradeOps.Domain.Oms.AccountKey scope,
            int maximumRows = 10000, CancellationToken cancellationToken = default)
        {
            if (Snapshot is not null) throw new InvalidOperationException("Recovery attempted to mix independent journal snapshots.");
            var raw = await inner.ReadAsync(scope, maximumRows, cancellationToken);
            Snapshot = raw with { Commands = raw.Commands.ToArray(), Problems = raw.Problems.ToArray(),
                CommandReceipts = raw.Receipts.ToArray(), Private = raw.Private with {
                    Observations = raw.Private.Observations.ToArray(), Problems = raw.Private.Problems.ToArray() } };
            return Snapshot;
        }
    }

    private sealed class RecordedAccount(IDeribitAccountSnapshotReader inner, Func<CancellationToken, Task>? before = null) : IDeribitAccountSnapshotReader
    {
        internal DeribitAccountSnapshotResult? Observed;
        public async Task<DeribitAccountSnapshotResult> ReadAsync(DeribitAccountSnapshotRequest request, CancellationToken ct = default)
        {
            if (Observed is not null) throw new InvalidOperationException("Recovery attempted to mix independent account reads.");
            if (before is not null) await before(ct);
            var raw = await inner.ReadAsync(request, ct);
            Observed = raw with { RequestedInstruments = raw.RequestedInstruments.ToArray(),
                Positions = raw.Positions.ToArray(), Problems = raw.Problems.ToArray() };
            return Observed;
        }
    }

    private sealed class NoIndependentPrivateRead : IOmsPrivateRecoveryReader
    {
        public Task<OmsPrivateRecoveryReadResult> ReadAsync(TradeOps.Domain.Oms.AccountKey scope, int maximumRows = 10000,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Recovery must use the combined journal snapshot.");
    }

    private sealed class RecordedQueries(IDeribitRecoveryQueries inner) : IDeribitRecoveryQueries
    {
        internal DeribitRecoveryQueryResult? Observed;
        public async Task<DeribitRecoveryQueryResult> CollectAsync(DeribitRecoveryQuery query, CancellationToken ct = default)
        {
            if (Observed is not null) throw new InvalidOperationException("Recovery query repeated unexpectedly.");
            var raw = await inner.CollectAsync(query, ct);
            Observed = raw with { Sources = raw.Sources.ToArray(), Observations = raw.Observations.ToArray(), Problems = raw.Problems.ToArray() };
            return Observed;
        }
    }

    private sealed class RecordedKnown(IDeribitKnownOrderReader inner) : IDeribitKnownOrderReader
    {
        internal DeribitKnownOrderReadResult? Observed;
        public async Task<DeribitKnownOrderReadResult> ReadAsync(DeribitKnownOrderReadRequest request, CancellationToken ct = default)
        {
            if (Observed is not null) throw new InvalidOperationException("Known-order query repeated unexpectedly.");
            var raw = await inner.ReadAsync(request, ct);
            Observed = raw with { Lookups = raw.Lookups.ToArray(), Observations = raw.Observations.ToArray(), Problems = raw.Problems.ToArray() };
            return Observed;
        }
    }

    private sealed class UniverseRead(DeribitRecoveryUniverseSources sources, OmsRecoveryRunStart start,
        RecordedLedger ledger, TimeSpan span, TimeProvider time)
    {
        private readonly DeribitRecoveryInstrument[] manifest = sources.TrustedManifest.Take(4097).ToArray();
        internal DeribitAccountInventoryResult? Inventory;
        internal readonly List<DeribitCurrencyRecoveryResult> CurrencyReads = [];
        private DeribitRecoveryUniverse? universe;
        private readonly List<DeribitRecoveryUniverseIssue> issues = [];
        internal async Task ReadAsync(CancellationToken ct)
        {
            var raw = await sources.Inventory.ReadAsync(new(start.Scope, start.Epoch, span), ct);
            var observed = raw with { Balances = raw.Balances.ToArray(), Positions = raw.Positions.ToArray(),
                OpenOrders = raw.OpenOrders.ToArray(), Problems = raw.Problems.ToArray() };
            universe = DeribitRecoveryUniverseBuilder.Build(start.Scope, start.Epoch, ledger.Snapshot!, observed, manifest, time.GetUtcNow());
            // A misbound port result must not archive another account's private data under this scope.
            if (observed.Scope == start.Scope && observed.Epoch == start.Epoch) Inventory = observed;
            if (!universe.UniverseResolved) return;
            var remaining = DeribitOrderRecoveryProjector.MaximumFactsPerSet;
            foreach (var currency in universe.RequiredCurrencies)
            {
                if (remaining == 0) { issues.Add(new(DeribitRecoveryUniverseProblem.Capacity)); break; }
                var rawQuery = await sources.CurrencyQueries.CollectAsync(new(start.Scope, start.Epoch, currency, "any",
                    start.FromUtc, start.ThroughUtc, TimeSpan.FromMinutes(2), MaximumFacts: remaining), ct);
                var query = rawQuery with { Sources = rawQuery.Sources.ToArray(), Observations = rawQuery.Observations.ToArray(), Problems = rawQuery.Problems.ToArray() };
                if (query.Scope != start.Scope || query.Epoch != start.Epoch || query.Currency != currency || query.Kind != "any" ||
                    query.Observations.Any(x => x.Scope != start.Scope || x.Observation.StreamEpoch != start.Epoch))
                { issues.Add(new(DeribitRecoveryUniverseProblem.CurrencyHistoryUnavailable, Currency: currency)); break; }
                CurrencyReads.Add(query); remaining -= query.Observations.Count;
                if (!query.TraversalComplete || remaining < 0)
                { issues.Add(new(DeribitRecoveryUniverseProblem.CurrencyHistoryUnavailable, Currency: currency)); break; }
            }
        }
        internal DeribitRecoveryUniverse? Finish()
        {
            if (universe is null) return null;
            // Check freshness at use time, after every query and capture drain has completed.
            var final = Inventory is null ? universe : DeribitRecoveryUniverseBuilder.Build(start.Scope, start.Epoch,
                ledger.Snapshot!, Inventory, manifest, time.GetUtcNow());
            return final with { Issues = final.Issues.Concat(issues).Distinct().ToArray() };
        }
    }
}
