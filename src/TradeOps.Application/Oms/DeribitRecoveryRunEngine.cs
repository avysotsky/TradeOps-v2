namespace TradeOps.Application.Oms;

public sealed record DeribitRecoveryUniverseSources(IDeribitAccountInventoryReader Inventory,
    IDeribitCurrencyRecoveryQueries CurrencyQueries, IReadOnlyList<DeribitRecoveryInstrument> TrustedManifest,
    IDeribitRecoveryClockReader? Clock = null);

public sealed record DeribitRecoveryRunOutcome(DeribitRecoveryRunDocumentV1 Document,
    DeribitRecoveryCoherenceAssessment Coherence,
    [property: System.Text.Json.Serialization.JsonIgnore] OmsRuntimeRecoveryGate? RuntimeGate);

/// <summary>Durable lifecycle around the actual ledger/account/replay gate. No dispatch, caller-provided
/// completeness flags, swallowed errors or READY shortcut. Existing read-only hosts remain read-only.</summary>
public sealed class DeribitRecoveryRunEngine(IOmsRecoveryRunStore store, IOmsRecoveryLedgerReader ledger,
    IDeribitRecoveryQueries queries, IDeribitKnownOrderReader known, IDeribitAccountSnapshotReader account,
    TimeProvider? clock = null, DeribitRecoveryUniverseSources? universeSources = null)
{
    public async Task<DeribitRecoveryRunDocumentV1> RunAsync(DeribitRecoveryGateRequest request,
        DeribitRecoveryAccountRequest accountRequest, OmsPrivateCaptureCoordinator coordinator,
        OmsPrivateCaptureSession session, DeribitReplayDrainOptions? drainOptions = null, CancellationToken ct = default)
        => (await RunCoreAsync(request, accountRequest, coordinator, session, false, drainOptions, ct)).Document;

    public Task<DeribitRecoveryRunOutcome> RecoverAndPublishAsync(DeribitRecoveryGateRequest request,
        DeribitRecoveryAccountRequest accountRequest, OmsPrivateCaptureCoordinator coordinator,
        OmsPrivateCaptureSession session, DeribitReplayDrainOptions? drainOptions = null, CancellationToken ct = default)
    {
        if (store is not IOmsRecoveredAccountStore) throw new InvalidOperationException("Qualified publication store required.");
        return RunCoreAsync(request, accountRequest, coordinator, session, true, drainOptions, ct);
    }

    private async Task<DeribitRecoveryRunOutcome> RunCoreAsync(DeribitRecoveryGateRequest request,
        DeribitRecoveryAccountRequest accountRequest, OmsPrivateCaptureCoordinator coordinator,
        OmsPrivateCaptureSession session, bool publish, DeribitReplayDrainOptions? drainOptions, CancellationToken ct)
    {
        var q = request.Query;
        var start = new OmsRecoveryRunStart(q.Scope, Guid.NewGuid(), q.Epoch, q.NativeInstrument,
            request.Currency, q.FromUtc, q.ThroughUtc);
        var frozenUniverseSources = universeSources is null ? null : universeSources with {
            TrustedManifest = Array.AsReadOnly(universeSources.TrustedManifest.Take(4097).ToArray()) };
        accountRequest = accountRequest with { ExpectedInstruments = Array.AsReadOnly(accountRequest.ExpectedInstruments.Take(257).ToArray()) };
        start.Validate(); ct.ThrowIfCancellationRequested();
        var token = await store.BeginAsync(start, ct);
        try
        {
            await store.MarkReconcilingAsync(token, ct);
            var recordedLedger = new RecordedLedger(ledger);
            var universeRead = frozenUniverseSources is null ? null : new UniverseRead(frozenUniverseSources, start, recordedLedger,
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
                Universe: universeRead?.Finish(), CurrencyObservations: universeRead?.CurrencyReads.ToArray(),
                InitialInventoryObservations: universeRead?.InitialInventory, VenueClock: universeRead?.Clock);
            document = document with { Candidate = DeribitRecoveryCandidateBuilder.Build(document) };
            document.Validate();
            var coherence = DeribitRecoveryCoherencePolicy.Assess(document, clock);
            document = coherence.Document;
            OmsRuntimeRecoveryGate? runtime = null;
            if (publish && coherence.Publication is { } qualified && store is IOmsRecoveredAccountStore publisher)
            {
                var publication = await publisher.PublishAsync(token, document, qualified, ct);
                runtime = new(publisher, qualified, publication);
            }
            else await store.CompleteBlockedAsync(token, document, ct);
            return new(document, coherence.Assessment, runtime);
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
            Snapshot = raw with { Commands = raw.Commands.ToArray(), Problems = raw.Problems.ToArray(), OperatorReviews = raw.OperatorReviews?.ToArray(),
                RecoveryArchive = raw.RecoveryArchive is null ? null : raw.RecoveryArchive with { Facts = Array.AsReadOnly(raw.RecoveryArchive.Facts.ToArray()) },
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
        internal DeribitAccountInventoryResult? InitialInventory;
        internal DeribitRecoveryClockObservation? Clock;
        private DateTimeOffset queryThrough = DateTimeOffset.FromUnixTimeMilliseconds(start.ThroughUtc.ToUnixTimeMilliseconds());
        internal readonly List<DeribitCurrencyRecoveryResult> CurrencyReads = [];
        private DeribitRecoveryUniverse? universe;
        private readonly List<DeribitRecoveryUniverseIssue> issues = [];
        internal async Task ReadAsync(CancellationToken ct)
        {
            var calledInventory = time.GetUtcNow();
            var raw = await sources.Inventory.ReadAsync(new(start.Scope, start.Epoch, span), ct);
            var receivedInventory = time.GetUtcNow();
            var observed = raw with { Balances = raw.Balances.ToArray(), Positions = raw.Positions.ToArray(),
                OpenOrders = raw.OpenOrders.ToArray(), Problems = raw.Problems.ToArray() };
            universe = DeribitRecoveryUniverseBuilder.Build(start.Scope, start.Epoch, ledger.Snapshot!, observed, manifest, time.GetUtcNow());
            // A misbound port result must not archive another account's private data under this scope.
            if (observed.Scope == start.Scope && observed.Epoch == start.Epoch) InitialInventory = Inventory = observed;
            if (observed.StartedAtUtc < calledInventory || observed.CompletedAtUtc > receivedInventory)
            { issues.Add(new(DeribitRecoveryUniverseProblem.InvalidInventory)); return; }
            if (!universe.UniverseResolved) return;
            if (sources.Clock is not null)
            {
                var calledClock = time.GetUtcNow(); var clock = await sources.Clock.ReadAsync(new(start.Scope, start.Epoch), ct); var receivedClock = time.GetUtcNow();
                if (clock.Scope == start.Scope && clock.Epoch == start.Epoch) Clock = clock;
                if (clock.Scope != start.Scope || clock.Epoch != start.Epoch || clock.RequestedAtUtc < calledClock ||
                    clock.ReceivedAtUtc < clock.RequestedAtUtc || clock.ReceivedAtUtc > receivedClock || clock.VenueTimeUtc is not { } native ||
                    native.Offset != TimeSpan.Zero || native < clock.RequestedAtUtc.AddSeconds(-2) || native > clock.ReceivedAtUtc ||
                    native < queryThrough || clock.ReceivedAtUtc-clock.RequestedAtUtc > TimeSpan.FromSeconds(2))
                { issues.Add(new(DeribitRecoveryUniverseProblem.VenueClockUnavailable)); return; }
                queryThrough = native;
            }
            var remaining = DeribitOrderRecoveryProjector.MaximumFactsPerSet;
            foreach (var currency in universe.RequiredCurrencies)
            {
                if (remaining == 0) { issues.Add(new(DeribitRecoveryUniverseProblem.Capacity)); break; }
                var called = time.GetUtcNow();
                var rawQuery = await sources.CurrencyQueries.CollectAsync(new(start.Scope, start.Epoch, currency, "any",
                    start.FromUtc, queryThrough, TimeSpan.FromMinutes(2), MaximumFacts: remaining), ct);
                var received = time.GetUtcNow();
                var query = rawQuery with { Sources = rawQuery.Sources.ToArray(), Observations = rawQuery.Observations.ToArray(),
                    Problems = rawQuery.Problems.ToArray(), UnmappedInstruments = rawQuery.UnmappedInstruments?.ToArray() };
                if (!ValidQuery(query, currency, called, received, remaining))
                { issues.Add(new(DeribitRecoveryUniverseProblem.CurrencyHistoryUnavailable, Currency: currency)); break; }
                foreach (var name in query.UnmappedInstruments ?? [])
                    issues.Add(new(DeribitRecoveryUniverseProblem.UnmappedInstrument, name, currency));
                CurrencyReads.Add(query); remaining -= query.Observations.Count;
                if (!query.TraversalComplete || remaining < 0)
                { issues.Add(new(DeribitRecoveryUniverseProblem.CurrencyHistoryUnavailable, Currency: currency)); break; }
            }
            calledInventory = time.GetUtcNow();
            var finalRaw = await sources.Inventory.ReadAsync(new(start.Scope, start.Epoch, span), ct);
            receivedInventory = time.GetUtcNow();
            var finalInventory = finalRaw with { Balances = finalRaw.Balances.ToArray(), Positions = finalRaw.Positions.ToArray(),
                OpenOrders = finalRaw.OpenOrders.ToArray(), Problems = finalRaw.Problems.ToArray() };
            var finalUniverse = DeribitRecoveryUniverseBuilder.Build(start.Scope, start.Epoch, ledger.Snapshot!, finalInventory, manifest, time.GetUtcNow());
            if (finalInventory.Scope == start.Scope && finalInventory.Epoch == start.Epoch) Inventory = finalInventory;
            if (finalInventory.StartedAtUtc < calledInventory || finalInventory.CompletedAtUtc > receivedInventory)
                issues.Add(new(DeribitRecoveryUniverseProblem.InvalidInventory));
            issues.AddRange(finalUniverse.Issues);
            if (finalUniverse.UniverseResolved && !DeribitAccountInventoryComparison.SameEconomicState(observed, finalInventory))
                issues.Add(new(DeribitRecoveryUniverseProblem.AccountObservationsChanged));
        }
        internal DeribitRecoveryUniverse? Finish()
        {
            if (universe is null) return null;
            // Check freshness at use time, after every query and capture drain has completed.
            var final = Inventory is null ? universe : DeribitRecoveryUniverseBuilder.Build(start.Scope, start.Epoch,
                ledger.Snapshot!, Inventory, manifest, time.GetUtcNow());
            var symbols = final.RequiredInstruments.Concat(CurrencyReads.SelectMany(x => x.Observations).Select(x => x.Facts.NativeInstrument))
                .Concat(CurrencyReads.SelectMany(x => x.UnmappedInstruments ?? [])).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return final with { Issues = final.Issues.Concat(issues).Distinct().ToArray(), RequiredInstruments = symbols,
                InstrumentTerms = manifest.Where(x => symbols.Contains(x.NativeInstrument, StringComparer.Ordinal)).OrderBy(x => x.NativeInstrument, StringComparer.Ordinal).ToArray() };
        }

        private bool ValidQuery(DeribitCurrencyRecoveryResult q, string currency, DateTimeOffset called, DateTimeOffset received, int maximum)
        {
            string[] names = ["open", "orders-recent", "orders-historical", "trades-recent", "trades-historical"];
            if (q.Scope != start.Scope || q.Epoch != start.Epoch || q.Currency != currency || q.Kind != "any" ||
                q.QueryFromUtc.Offset != TimeSpan.Zero || q.ThroughUtc.Offset != TimeSpan.Zero ||
                q.QueryFromUtc != DateTimeOffset.FromUnixTimeMilliseconds(start.FromUtc.AddMinutes(-2).ToUnixTimeMilliseconds()) ||
                q.ThroughUtc != queryThrough ||
                q.StartedAtUtc.Offset != TimeSpan.Zero || q.CompletedAtUtc.Offset != TimeSpan.Zero ||
                q.StartedAtUtc < called || q.CompletedAtUtc < q.StartedAtUtc || q.CompletedAtUtc > received ||
                q.Observations.Count > maximum || q.Problems.Count > 8 || q.Problems.Any(x => !Enum.IsDefined(x)) ||
                q.Sources.Count > 5 || q.Sources.Select(x => x.Source).Distinct(StringComparer.Ordinal).Count() != q.Sources.Count ||
                q.Sources.Any(x => !names.Contains(x.Source, StringComparer.Ordinal) || x.Pages is < 0 or > 20 || x.Exhausted && x.Pages == 0) ||
                q.Problems.Count == 0 && !q.TraversalComplete || q.Problems.Count != 0 && q.Observations.Count != 0 ||
                q.UnmappedInstruments is not null && (q.UnmappedInstruments.Count > 32 || q.UnmappedInstruments.Any(x =>
                    string.IsNullOrWhiteSpace(x) || x.Length > 160 || x != x.Trim() || x.Any(char.IsControl)))) return false;
            var terms = manifest.ToDictionary(x => x.NativeInstrument, StringComparer.Ordinal);
            var hashes = new Dictionary<(string, string), byte[]>();
            try
            {
                foreach (var row in q.Observations)
                {
                    row.Validate(); var f = row.Facts; var o = row.Observation;
                    var open = o.Channel == "private/get_open_orders_by_currency";
                    var trades = o.Channel == "private/get_user_trades_by_currency";
                    if (row.Scope != start.Scope || o.StreamEpoch != start.Epoch || o.Source != OmsPrivateCaptureSource.RecoveryQuery ||
                        !open && !trades && o.Channel != "private/get_order_history_by_currency" ||
                        o.ReceivedAtUtc < q.StartedAtUtc || o.ReceivedAtUtc > q.CompletedAtUtc || f.VenueTimestampUtc > o.ReceivedAtUtc ||
                        f.Currency != currency || !terms.TryGetValue(f.NativeInstrument, out var m) || m.Currency != currency ||
                        f.AmountUnit != m.AmountUnit || f.AmountCurrency != m.AmountCurrency ||
                        trades != (f.Kind == OmsJournalEventKind.Execution) || trades && (f.Fee is null || f.FeeCurrency is null) ||
                        open && f.OrderState is not ("open" or "triggered" or "untriggered") ||
                        !open && (f.VenueTimestampUtc < q.QueryFromUtc || f.VenueTimestampUtc > q.ThroughUtc)) return false;
                    var encoded = OmsPrivateEventCanonical.Encode(row); var key = (encoded.Namespace, encoded.EventId);
                    if (hashes.TryGetValue(key, out var prior) && !prior.SequenceEqual(encoded.Sha256)) return false;
                    hashes[key] = encoded.Sha256;
                }
            }
            catch (ArgumentException) { return false; }
            return true;
        }

    }
}
