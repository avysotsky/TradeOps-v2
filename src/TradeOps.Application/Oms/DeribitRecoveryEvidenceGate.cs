using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public enum DeribitRecoveryEvidenceBlocker
{
    SnapshotUnusable, SnapshotBindingInvalid, RetainedEvidenceInvalid, KnownOrderSelectionInvalid,
    QueryFailed, QueryBindingInvalid, QueryWindowInvalid, QuerySourcesInvalid, QueryEvidenceInvalid,
    HistoricalUnfilledCoverageGap, OrderHistoryIncomplete, CoherentCutUnavailable, BufferedReplayUnavailable,
    ExecutionLedgerCoverageUnproven, ExternalOrderEvidence, KnownOrderRetentionExpired,
    KnownOrderUnavailable, KnownReadFailed, KnownReadBindingInvalid, KnownReadEvidenceInvalid,
    BufferedReplayInvalid, BufferedReplayDrainTimedOut,
    AccountObservationsUnavailable, AccountReadFailed, AccountReadSkipped, AccountBindingInvalid,
    AccountCoverageInvalid, AccountObservationInvalid, AccountObservationStale,
    AccountSnapshotCoherenceUnproven, AccountWideCoverageUnproven
}

/// <summary>Trusted instrument units/currency, never derived from a bare symbol or returned facts.</summary>
public sealed record DeribitRecoveryGateRequest(DeribitRecoveryQuery Query, string Currency,
    OmsPrivateAmountUnit AmountUnit, string AmountCurrency);

public sealed record DeribitRecoveryGateAssessment(AccountKey Scope, Guid Epoch, Guid SnapshotId,
    string NativeInstrument, DateTimeOffset FromUtc, DateTimeOffset ThroughUtc,
    DateTimeOffset AssessedAtUtc, int RetainedFacts, int EnumeratedFacts, int KnownOrders,
    int AddressedOrders, int RetentionExpiredOrders, IReadOnlyList<DeribitRecoveryEvidenceBlocker> Blockers)
{
    public bool CanRebuildOrders => Blockers.Count == 0;
    public bool AccountReady => false;
    public bool DurableProjectionPublished => false;
}

/// <summary>
/// Read-side evidence orchestration only. No writes, reducer invocation, readiness transition or sender.
/// Current query ports cannot establish a coherent replay cut or complete historical coverage.
/// </summary>
public sealed partial class DeribitRecoveryEvidenceGate(IOmsPrivateRecoveryReader retainedReader,
    IDeribitRecoveryQueries queries, IDeribitKnownOrderReader knownReader, TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private static readonly string[] SourceNames = ["open", "orders-recent", "orders-historical", "trades-recent", "trades-historical"];
    private static readonly string[] QueryMethods = ["private/get_open_orders_by_instrument", "private/get_order_history_by_instrument", "private/get_user_trades_by_instrument"];

    public async Task<DeribitRecoveryGateAssessment> AssessAsync(DeribitRecoveryGateRequest request, CancellationToken ct = default)
    {
        var started = time.GetUtcNow();
        var q = ValidateRequest(request, started);
        var blockers = new HashSet<DeribitRecoveryEvidenceBlocker> {
            DeribitRecoveryEvidenceBlocker.CoherentCutUnavailable, DeribitRecoveryEvidenceBlocker.BufferedReplayUnavailable,
            DeribitRecoveryEvidenceBlocker.ExecutionLedgerCoverageUnproven };
        ct.ThrowIfCancellationRequested();
        var snapshot = await retainedReader.ReadAsync(q.Scope, cancellationToken: ct);
        var retainedCount = 0; var enumeratedCount = 0; var knownCount = 0; var addressedCount = 0; var expired = 0;
        if (!snapshot.IsUsable) blockers.Add(DeribitRecoveryEvidenceBlocker.SnapshotUnusable);
        if (snapshot.Scope != q.Scope || snapshot.SnapshotId == Guid.Empty || !Utc(snapshot.ReadAtUtc) ||
            snapshot.ReadAtUtc < started.AddSeconds(-5) || snapshot.ReadAtUtc > time.GetUtcNow().AddSeconds(5))
            blockers.Add(DeribitRecoveryEvidenceBlocker.SnapshotBindingInvalid);
        if (snapshot.AcceptedFactCount is < 0 or > 10000 || snapshot.Observations.Count > 10000)
            blockers.Add(DeribitRecoveryEvidenceBlocker.RetainedEvidenceInvalid);
        if (BadRetained()) return Finish(); // Invalid retained evidence never triggers venue requests.
        var retained = snapshot.Observations.ToArray();
        var canonical = new Dictionary<(string, string), byte[]>();
        foreach (var row in retained)
        {
            if (row.Disposition is not (OmsPrivateCaptureResult.Inserted or OmsPrivateCaptureResult.Duplicate) ||
                !ValidEnvelope(row.Envelope, null, snapshot.ReadAtUtc.AddSeconds(5), false))
            { blockers.Add(DeribitRecoveryEvidenceBlocker.RetainedEvidenceInvalid); break; }
            var encoded = OmsPrivateEventCanonical.Encode(row.Envelope); var key = (encoded.Namespace, encoded.EventId);
            if (canonical.TryGetValue(key, out var prior) && !prior.SequenceEqual(encoded.Sha256))
            { blockers.Add(DeribitRecoveryEvidenceBlocker.RetainedEvidenceInvalid); break; }
            canonical[key] = encoded.Sha256;
        }
        if (canonical.Count != snapshot.AcceptedFactCount) blockers.Add(DeribitRecoveryEvidenceBlocker.RetainedEvidenceInvalid);
        if (BadRetained()) return Finish();
        retainedCount = canonical.Count;
        DeribitKnownOrderReadRequest? seeds = null;
        if (retained.Any(x => x.Envelope.Facts.NativeInstrument == q.NativeInstrument))
        {
            try { seeds = DeribitKnownOrderSelection.FromSnapshot(snapshot with { Observations = retained }, q.Scope, q.Epoch, q.NativeInstrument); }
            catch (InvalidOperationException) { blockers.Add(DeribitRecoveryEvidenceBlocker.KnownOrderSelectionInvalid); return Finish(); }
            knownCount = seeds.Orders.Count;
            if (seeds.Orders.Any(x => !Terms(x))) { blockers.Add(DeribitRecoveryEvidenceBlocker.RetainedEvidenceInvalid); return Finish(); }
        }
        var queryStarted = time.GetUtcNow();
        ct.ThrowIfCancellationRequested();
        var batch = await queries.CollectAsync(q, ct);
        if (batch.Problems.Count != 0) blockers.Add(DeribitRecoveryEvidenceBlocker.QueryFailed);
        if (batch.Scope != q.Scope || batch.Epoch != q.Epoch || batch.NativeInstrument != q.NativeInstrument || batch.Currency != request.Currency ||
            !Times(batch.StartedAtUtc, batch.CompletedAtUtc, queryStarted)) blockers.Add(DeribitRecoveryEvidenceBlocker.QueryBindingInvalid);
        if (batch.QueryFromUtc != DateTimeOffset.FromUnixTimeMilliseconds((q.FromUtc-q.Overlap).ToUnixTimeMilliseconds()) ||
            batch.ThroughUtc != DateTimeOffset.FromUnixTimeMilliseconds(q.ThroughUtc.ToUnixTimeMilliseconds()))
            blockers.Add(DeribitRecoveryEvidenceBlocker.QueryWindowInvalid);
        if (batch.Sources.Count != 5 || !batch.Sources.Select(x => x.Source).Order(StringComparer.Ordinal).SequenceEqual(SourceNames.Order(StringComparer.Ordinal)) ||
            batch.Sources.Any(x => !x.Exhausted || x.Pages < 1 || x.Pages > q.MaximumPagesPerSource))
            blockers.Add(DeribitRecoveryEvidenceBlocker.QuerySourcesInvalid);
        if (!batch.HistoricalUnfilledOrdersCovered) blockers.Add(DeribitRecoveryEvidenceBlocker.HistoricalUnfilledCoverageGap);
        if (!batch.OrderHistoryComplete) blockers.Add(DeribitRecoveryEvidenceBlocker.OrderHistoryIncomplete);
        if (blockers.Any(x => x is DeribitRecoveryEvidenceBlocker.QueryFailed or DeribitRecoveryEvidenceBlocker.QueryBindingInvalid or
            DeribitRecoveryEvidenceBlocker.QueryWindowInvalid or DeribitRecoveryEvidenceBlocker.QuerySourcesInvalid)) return Finish();
        if (batch.Observations.Count > q.MaximumFacts) { blockers.Add(DeribitRecoveryEvidenceBlocker.QueryEvidenceInvalid); return Finish(); }
        var current = batch.Observations.ToArray();
        if (current.Any(x => !ValidEnvelope(x, batch.StartedAtUtc, batch.CompletedAtUtc, true) ||
            !QueryMethods.Contains(x.Observation.Channel, StringComparer.Ordinal) || !Terms(x.Facts) ||
            (x.Facts.Kind == OmsJournalEventKind.Execution) != (x.Observation.Channel == "private/get_user_trades_by_instrument") ||
            (x.Observation.Channel != "private/get_open_orders_by_instrument" &&
                (x.Facts.VenueTimestampUtc < batch.QueryFromUtc || x.Facts.VenueTimestampUtc > batch.ThroughUtc)) ||
            (x.Observation.Channel == "private/get_open_orders_by_instrument" && x.Facts.OrderState is not ("open" or "triggered" or "untriggered"))))
        { blockers.Add(DeribitRecoveryEvidenceBlocker.QueryEvidenceInvalid); return Finish(); }
        var currentHashes = new Dictionary<(string, string), byte[]>();
        foreach (var envelope in current)
        {
            var encoded = OmsPrivateEventCanonical.Encode(envelope); var key = (encoded.Namespace, encoded.EventId);
            if (currentHashes.TryGetValue(key, out var prior) && !prior.SequenceEqual(encoded.Sha256))
            { blockers.Add(DeribitRecoveryEvidenceBlocker.QueryEvidenceInvalid); return Finish(); }
            currentHashes[key] = encoded.Sha256;
        }
        if (current.Where(x => x.Facts.Kind == OmsJournalEventKind.Order)
            .GroupBy(x => (x.Facts.Currency, x.Facts.ExchangeOrderId, x.Facts.VenueTimestampUtc)).Any(x => x.Select(y => y.Facts).Distinct().Count() > 1))
        { blockers.Add(DeribitRecoveryEvidenceBlocker.QueryEvidenceInvalid); return Finish(); }
        enumeratedCount = current.Length;
        var seedKeys = seeds?.Orders.Select(Key).ToHashSet() ?? [];
        if (current.Any(x => !seedKeys.Contains(Key(x.Facts)))) blockers.Add(DeribitRecoveryEvidenceBlocker.ExternalOrderEvidence);
        if (seeds is null) return Finish();
        var pending = new List<OmsPrivateVenueFacts>();
        foreach (var seed in seeds.Orders)
        {
            var latest = current.Where(x => x.Facts.Kind == OmsJournalEventKind.Order && Key(x.Facts) == Key(seed))
                .OrderBy(x => x.Facts.VenueTimestampUtc).LastOrDefault()?.Facts;
            if (latest is not null && Compatible(seed, latest)) continue;
            if (latest is not null && latest.VenueTimestampUtc >= seed.VenueTimestampUtc)
            { blockers.Add(DeribitRecoveryEvidenceBlocker.QueryEvidenceInvalid); return Finish(); }
            if (Terminal(seed.OrderState) && seed.VenueTimestampUtc <= time.GetUtcNow().AddMinutes(-30))
            {
                expired++; blockers.Add(DeribitRecoveryEvidenceBlocker.KnownOrderRetentionExpired);
                blockers.Add(DeribitRecoveryEvidenceBlocker.KnownOrderUnavailable); continue;
            }
            pending.Add(seed);
        }
        if (pending.Count == 0) return Finish();
        addressedCount = pending.Count;
        var lookupRequest = seeds with { Orders = pending.AsReadOnly() };
        var lookupStarted = time.GetUtcNow();
        ct.ThrowIfCancellationRequested();
        var lookup = await knownReader.ReadAsync(lookupRequest, ct);
        if (lookup.Problems.Count != 0) blockers.Add(DeribitRecoveryEvidenceBlocker.KnownReadFailed);
        if (lookup.Scope != q.Scope || lookup.Epoch != q.Epoch || lookup.SnapshotId != snapshot.SnapshotId ||
            lookup.RequestedOrders != pending.Count || !Times(lookup.StartedAtUtc, lookup.CompletedAtUtc, lookupStarted))
            blockers.Add(DeribitRecoveryEvidenceBlocker.KnownReadBindingInvalid);
        if (lookup.Lookups.Count != pending.Count || lookup.Lookups.Select(x => x.RequestIndex).Order().Where((x, i) => x != i).Any() ||
            lookup.Lookups.Any(x => !Enum.IsDefined(x.Outcome))) blockers.Add(DeribitRecoveryEvidenceBlocker.KnownReadEvidenceInvalid);
        if (!lookup.AllOrdersFound) blockers.Add(DeribitRecoveryEvidenceBlocker.KnownOrderUnavailable);
        if (lookup.AllOrdersFound && (lookup.Observations.Count != pending.Count || pending.Any(seed =>
                lookup.Observations.Count(x => x.Facts.Kind == OmsJournalEventKind.Order && Key(x.Facts) == Key(seed) && Compatible(seed, x.Facts)) != 1)))
            blockers.Add(DeribitRecoveryEvidenceBlocker.KnownReadEvidenceInvalid);
        if (lookup.Observations.Count > 32 || lookup.Observations.Any(x =>
                !ValidEnvelope(x, lookup.StartedAtUtc, lookup.CompletedAtUtc, true) || !Terms(x.Facts) || x.Observation.Channel != "private/get_order_state") ||
            (!lookup.AllOrdersFound && lookup.Observations.Count != 0)) blockers.Add(DeribitRecoveryEvidenceBlocker.KnownReadEvidenceInvalid);
        return Finish();

        DeribitRecoveryGateAssessment Finish()
        {
            ct.ThrowIfCancellationRequested();
            return new(q.Scope, q.Epoch, snapshot.SnapshotId, q.NativeInstrument, q.FromUtc, q.ThroughUtc,
                time.GetUtcNow(), retainedCount, enumeratedCount, knownCount, addressedCount, expired,
                Array.AsReadOnly(blockers.Order().ToArray()));
        }
        bool BadRetained() => blockers.Any(x => x is DeribitRecoveryEvidenceBlocker.SnapshotUnusable or
            DeribitRecoveryEvidenceBlocker.SnapshotBindingInvalid or DeribitRecoveryEvidenceBlocker.RetainedEvidenceInvalid);
        bool Times(DateTimeOffset from, DateTimeOffset through, DateTimeOffset call) => Utc(from) && Utc(through) &&
            from >= call && through >= from && through <= time.GetUtcNow();
        bool Terms(OmsPrivateVenueFacts facts) => facts.NativeInstrument == q.NativeInstrument && facts.Currency == request.Currency &&
            facts.AmountUnit == request.AmountUnit && facts.AmountCurrency == request.AmountCurrency;
        bool ValidEnvelope(OmsPrivateEventEnvelope envelope, DateTimeOffset? receivedFrom, DateTimeOffset receivedThrough, bool recovery)
        {
            try { envelope.Validate(); }
            catch (ArgumentException) { return false; }
            return envelope.Scope == q.Scope && envelope.Facts.VenueTimestampUtc <= envelope.Observation.ReceivedAtUtc &&
                envelope.Observation.ReceivedAtUtc <= receivedThrough && (!receivedFrom.HasValue || envelope.Observation.ReceivedAtUtc >= receivedFrom) &&
                (!recovery || envelope.Observation.StreamEpoch == q.Epoch && envelope.Observation.Source == OmsPrivateCaptureSource.RecoveryQuery);
        }
    }

    private static DeribitRecoveryQuery ValidateRequest(DeribitRecoveryGateRequest request, DateTimeOffset started)
    {
        ArgumentNullException.ThrowIfNull(request);
        var q = request.Query ?? throw new ArgumentException("Recovery query required.");
        if (q.Scope.Venue.Value != "DERIBIT" || q.Scope.Environment is not (OmsEnvironment.Mainnet or OmsEnvironment.Testnet) ||
            q.Epoch == Guid.Empty || !Utc(q.FromUtc) || !Utc(q.ThroughUtc) || q.FromUtc > q.ThroughUtc ||
            q.ThroughUtc > started || q.FromUtc <= DateTimeOffset.UnixEpoch.AddHours(1) || q.ThroughUtc-q.FromUtc > TimeSpan.FromDays(7) ||
            q.Overlap < TimeSpan.FromMilliseconds(1) || q.Overlap > TimeSpan.FromHours(1) ||
            q.PageSize is < 1 or > 1000 || q.MaximumPagesPerSource is < 1 or > 20 || q.MaximumFacts is < 1 or > 10000 ||
            string.IsNullOrWhiteSpace(q.NativeInstrument) || q.NativeInstrument.Length > 160 || q.NativeInstrument.Any(char.IsControl) ||
            !Token(request.Currency) || !Token(request.AmountCurrency) || !Enum.IsDefined(request.AmountUnit) ||
            request.AmountUnit == OmsPrivateAmountUnit.Usd && request.AmountCurrency != "USD")
            throw new ArgumentException("Invalid trusted recovery gate request.");
        return q;
    }

    private static bool Utc(DateTimeOffset value) => value.Offset == TimeSpan.Zero && value > DateTimeOffset.UnixEpoch;
    private static bool Token(string value) => !string.IsNullOrEmpty(value) && value.Length <= 32 && value.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_');
    private static (string, string) Key(OmsPrivateVenueFacts facts) => (facts.Currency, facts.ExchangeOrderId);
    private static bool Terminal(string? state) => state is "cancelled" or "filled" or "rejected";
    private static bool Compatible(OmsPrivateVenueFacts seed, OmsPrivateVenueFacts current) => current.NativeInstrument == seed.NativeInstrument &&
        current.Side == seed.Side && current.NativeAmount == seed.NativeAmount && current.AmountUnit == seed.AmountUnit && current.AmountCurrency == seed.AmountCurrency &&
        current.VenueTimestampUtc >= seed.VenueTimestampUtc && current.FilledNativeAmount.HasValue && current.FilledNativeAmount >= seed.FilledNativeAmount.GetValueOrDefault() &&
        (!Terminal(seed.OrderState) || Terminal(current.OrderState));
}
