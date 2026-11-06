using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public enum DeribitRecoveryCoherenceProblem
{
    MissingLiveCapture = 1, CaptureChanged, ActivityDuringRecovery, InventoryUnavailable, InventoryChanged,
    NativeClockUnavailable, HistoryOutsideQualifiedRetention, CurrencyCoverageUnavailable, ModelUnavailable,
    JournalUnavailable, EvidenceVersionUnavailable, UnresolvedCommand, UnknownExternalOrder, InconsistentOrderLedger, StaleEvidence
}
public sealed record DeribitRecoveredNativeOrder(DeribitRecoveredOrder State, OmsPrivateVenueFacts LastReport, OmsLocalOrderId? LocalOrderId);
public sealed record OmsRecoveredAccountStateV1(int SchemaVersion, AccountKey Scope, Guid Epoch, Guid RunId, string Policy,
    DateTimeOffset AssessedAtUtc, DateTimeOffset RequiredFromUtc, DateTimeOffset NativeThroughUtc,
    IReadOnlyList<DeribitRecoveredNativeOrder> Orders, IReadOnlyList<OmsPrivateVenueFacts> Executions,
    IReadOnlyList<DeribitAccountPosition> Positions, IReadOnlyList<DeribitCurrencyBalance> Balances,
    IReadOnlyList<OmsCommandId> UnsentCommands, IReadOnlyList<Guid> OperatorDecisions, DateTimeOffset UsableThroughUtc);
public sealed record OmsRecoveryCommandResolution(OmsCommandId CommandId, OmsRecoveryDispatchPhase ExpectedPhase);
public sealed record DeribitRecoveryCoherenceAssessment(string Policy, DateTimeOffset AssessedAtUtc,
    IReadOnlyList<DeribitRecoveryCoherenceProblem> Problems)
{
    public bool EvidenceSufficient => Problems.Count == 0;
}

/// <summary>Only this policy can construct a publication candidate. Saved JSON/assessment booleans
/// cannot restore its live capture revision. It never authorizes an exchange send.</summary>
public sealed class OmsQualifiedRecovery
{
    public OmsRecoveryRunStart Start { get; }
    public long ExpectedEvidenceVersion { get; }
    public DateTimeOffset NativeThroughUtc { get; }
    public string StateJson { get; }
    public byte[] StateSha256 => stateSha256.ToArray();
    public IReadOnlyList<OmsRecoveryCommandResolution> CommandResolutions { get; }
    public byte[] EvidenceSha256 => evidenceSha256.ToArray();
    private readonly byte[] evidenceSha256;
    private readonly byte[] stateSha256;
    private readonly OmsPrivateAccountRevision revision;
    private readonly TimeProvider time;
    private readonly DateTimeOffset assessed;
    private readonly DateTimeOffset usableThrough;
    internal OmsQualifiedRecovery(DeribitRecoveryRunDocumentV1 document, long version, OmsRecoveredAccountStateV1 state,
        IReadOnlyList<OmsRecoveryCommandResolution> resolutions, OmsPrivateAccountRevision revision, TimeProvider time)
    {
        Start = document.Start; NativeThroughUtc = state.NativeThroughUtc; ExpectedEvidenceVersion = version; evidenceSha256 = OmsRecoveryRunCodec.Encode(document).Sha256;
        var encoded = OmsRecoveryRunCodec.Encode(state);
        StateJson = encoded.Json; stateSha256 = encoded.Sha256; CommandResolutions = Array.AsReadOnly(resolutions.ToArray());
        this.revision = revision; this.time = time; assessed = state.AssessedAtUtc; usableThrough = state.UsableThroughUtc;
    }
    public bool IsCurrent => time.GetUtcNow() >= assessed && time.GetUtcNow() <= usableThrough &&
        revision.Owner.IsAccountRevisionCurrent(revision);
}

public sealed record DeribitRecoveryCoherenceResult(DeribitRecoveryCoherenceAssessment Assessment,
    [property: System.Text.Json.Serialization.JsonIgnore] OmsQualifiedRecovery? Publication,
    DeribitRecoveryRunDocumentV1 Document);

/// <summary>Conservative operational baseline: an actual quiet all-account stream, stable full
/// observations and a completely traversed recent history window. Long outages remain blocked;
/// historical-unfilled omission is never converted into a caller-provided completeness flag.</summary>
public static class DeribitRecoveryCoherencePolicy
{
    public const string Policy = "deribit-quiescent-recent-v1";
    public static DeribitRecoveryCoherenceResult Assess(DeribitRecoveryRunDocumentV1 document, TimeProvider? clock = null)
    {
        document.Validate(); var time = clock ?? TimeProvider.System; var now = time.GetUtcNow();
        var problems = new HashSet<DeribitRecoveryCoherenceProblem>();
        var replay = document.Assessment.Account.Replay; var revision = replay?.Revision;
        if (revision is null || revision.Profile != OmsAccountSubscriptionProfile.EconomicAccountV2 ||
            revision.Scope != document.Start.Scope || revision.Epoch != document.Start.Epoch ||
            revision.WindowId != replay!.ReplayWindowId || revision.ThroughReceipt != replay.ThroughReceipt)
            problems.Add(DeribitRecoveryCoherenceProblem.MissingLiveCapture);
        else if (!revision.Owner.IsAccountRevisionCurrent(revision)) problems.Add(DeribitRecoveryCoherenceProblem.CaptureChanged);
        if (replay is null || !replay.LocalReplayValidated || replay.BufferedObservations != 0 ||
            replay.AfterReceipt != replay.ThroughReceipt || replay.BeganAtUtc is null || replay.SealedAtUtc is null)
            problems.Add(DeribitRecoveryCoherenceProblem.ActivityDuringRecovery);
        var before = document.InitialInventoryObservations; var after = document.InventoryObservations;
        if (before is null || after is null || !before.IsFreshAt(now) || !after.IsFreshAt(now)) problems.Add(DeribitRecoveryCoherenceProblem.InventoryUnavailable);
        if (before is not null && after is not null && (replay?.BeganAtUtc is not { } opened || replay.SealedAtUtc is not { } sealedAtInventory ||
            before.StartedAtUtc < opened || before.CompletedAtUtc > after.StartedAtUtc || after.CompletedAtUtc > sealedAtInventory))
            problems.Add(DeribitRecoveryCoherenceProblem.InventoryUnavailable);
        if (before is not null && after is not null && !DeribitAccountInventoryComparison.SameEconomicState(before, after) ||
            document.Universe is null || !document.Universe.UniverseResolved || document.Candidate?.UniverseIssues?.Count > 0)
            problems.Add(DeribitRecoveryCoherenceProblem.InventoryChanged);
        var venue = document.VenueClock;
        if (venue?.VenueTimeUtc is not { } native || replay?.BeganAtUtc is not { } began || replay.SealedAtUtc is not { } sealedAt ||
            venue.RequestedAtUtc < began || venue.ReceivedAtUtc > sealedAt || venue.ReceivedAtUtc < venue.RequestedAtUtc ||
            before is not null && venue.RequestedAtUtc < before.CompletedAtUtc || after is not null && venue.ReceivedAtUtc > after.StartedAtUtc ||
            venue.ReceivedAtUtc-venue.RequestedAtUtc > TimeSpan.FromSeconds(2) || native > venue.ReceivedAtUtc || native < venue.RequestedAtUtc.AddSeconds(-2))
            problems.Add(DeribitRecoveryCoherenceProblem.NativeClockUnavailable);
        var batches = document.CurrencyObservations ?? [];
        if (batches.Count == 0 || document.Universe is null ||
            !batches.Select(x => x.Currency).Order(StringComparer.Ordinal).SequenceEqual(document.Universe.RequiredCurrencies.Order(StringComparer.Ordinal)) ||
            batches.Any(x => !x.TraversalComplete || x.Kind != "any" || x.QueryFromUtc > document.Start.FromUtc || x.ThroughUtc != venue?.VenueTimeUtc ||
                x.StartedAtUtc < venue?.ReceivedAtUtc || x.CompletedAtUtc < x.StartedAtUtc || x.CompletedAtUtc > after?.StartedAtUtc))
            problems.Add(DeribitRecoveryCoherenceProblem.CurrencyCoverageUnavailable);
        // Recent orders are retained for 30 minutes. A strict 25-minute bound at consumption
        // leaves a safety margin for overlap, I/O and clock uncertainty instead of claiming complete historical unfilled orders.
        if (venue?.VenueTimeUtc is { } server && (now < venue.RequestedAtUtc ||
            batches.Any(x => server + (now-venue.RequestedAtUtc)-x.QueryFromUtc > TimeSpan.FromMinutes(25))))
            problems.Add(DeribitRecoveryCoherenceProblem.HistoryOutsideQualifiedRetention);
        var model = document.Candidate;
        var rebuilt = DeribitRecoveryCandidateBuilder.Build(document);
        if (model is null || rebuilt is null || !OmsRecoveryRunCodec.Encode(model).Sha256.SequenceEqual(OmsRecoveryRunCodec.Encode(rebuilt).Sha256) ||
            model.CoverageScope != DeribitRecoveryCandidateScope.InventoryCurrencies || model.CurrencyBalances is null)
            problems.Add(DeribitRecoveryCoherenceProblem.ModelUnavailable);
        if (!document.Ledger.IsUsable) problems.Add(DeribitRecoveryCoherenceProblem.JournalUnavailable);
        if (document.Ledger.EvidenceVersion is null or < 0) problems.Add(DeribitRecoveryCoherenceProblem.EvidenceVersionUnavailable);
        if (now < document.Assessment.Ledger.Orders.AssessedAtUtc || now-document.Assessment.Ledger.Orders.AssessedAtUtc > TimeSpan.FromSeconds(5))
            problems.Add(DeribitRecoveryCoherenceProblem.StaleEvidence);
        if (model is null) return Finish(null);

        var all = OmsRecoveryRetainedFacts.Collect(document.Ledger, now)
            .Concat(batches.SelectMany(x => x.Observations)).Concat(document.KnownOrderObservations?.Observations ?? []).ToArray();
        var receipts = document.Ledger.Receipts;
        try { DeribitCommandReceiptSet.Validate(document.Start.Scope, document.Ledger.Commands, receipts, document.Ledger.Private.ReadAtUtc); }
        catch (ArgumentException) { problems.Add(DeribitRecoveryCoherenceProblem.JournalUnavailable); return Finish(null); }
        var owned = receipts.Where(x => x.Command.Payload.Kind == OmsJournalCommandKind.Place)
            .ToDictionary(x => (x.Order.Currency, x.Order.ExchangeOrderId));
        var reviews = document.Ledger.OperatorReviews ?? [];
        if (reviews.Count > 10000 || reviews.Select(x => (x.Currency, x.ExchangeOrderId)).Distinct().Count() != reviews.Count ||
            reviews.Any(x => x.SchemaVersion != 1 || x.Scope != document.Start.Scope || x.DecisionId == Guid.Empty || x.SourceRunId == Guid.Empty ||
                !Enum.IsDefined(x.Decision) || x.EvidenceVersion <= 0 || x.EvidenceVersion > document.Ledger.EvidenceVersion ||
                x.RecordedAtUtc.Offset != TimeSpan.Zero || x.RecordedAtUtc > document.Ledger.Private.ReadAtUtc.AddSeconds(5) ||
                x.EvidenceSha256.Length != 64 || !x.EvidenceSha256.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(x.ActorRole) || string.IsNullOrWhiteSpace(x.Reason)))
        { problems.Add(DeribitRecoveryCoherenceProblem.JournalUnavailable); return Finish(null); }
        var acceptedExternal = new HashSet<(string, string)>();
        var facts = all.GroupBy(x => (x.Facts.Currency, x.Facts.ExchangeOrderId)).ToDictionary(x => x.Key, x => x.Select(y => y.Facts).ToArray());
        var orders = new List<DeribitRecoveredNativeOrder>(); var terminalProven = new HashSet<(string, string)>();
        foreach (var order in model.Orders.Orders)
        {
            var key = (order.Currency, order.ExchangeOrderId);
            var reports = facts.GetValueOrDefault(key, []).Where(x => x.Kind == OmsJournalEventKind.Order).Distinct().OrderBy(x => x.VenueTimestampUtc).ToArray();
            var latest = reports.LastOrDefault();
            if (latest is null) { problems.Add(DeribitRecoveryCoherenceProblem.InconsistentOrderLedger); continue; }
            var state = order;
            if (order.NativeState is null && latest.OrderState is ("cancelled" or "filled" or "rejected") &&
                latest.FilledNativeAmount == order.KnownFilledNativeAmount && reports.All(x => x.FilledNativeAmount.HasValue) &&
                !facts[key].Any(x => x.Kind == OmsJournalEventKind.Execution && x.VenueTimestampUtc > latest.VenueTimestampUtc))
            {
                var firstTerminal = reports.First(x => x.OrderState is "cancelled" or "filled" or "rejected");
                if (reports.Where(x => x.VenueTimestampUtc >= firstTerminal.VenueTimestampUtc).All(x =>
                    x.OrderState == firstTerminal.OrderState && x.FilledNativeAmount == firstTerminal.FilledNativeAmount))
                { state = order with { NativeState = latest.OrderState }; terminalProven.Add(key); }
            }
            if (state.NativeState is null) problems.Add(DeribitRecoveryCoherenceProblem.InconsistentOrderLedger);
            var binding = owned.GetValueOrDefault(key);
            var review = reviews.SingleOrDefault(x => x.Currency == key.Currency && x.ExchangeOrderId == key.ExchangeOrderId);
            if (binding is null && review?.Decision == OmsRecoveryExternalOrderDecision.AcceptObservedExternalOrder &&
                now >= review.RecordedAtUtc && now-review.RecordedAtUtc <= TimeSpan.FromMinutes(10) &&
                OmsRecoveryExternalOrderEvidencePolicy.Find(document, order.Currency, order.ExchangeOrderId) is { } external &&
                OmsRecoveryExternalOrderEvidencePolicy.Digest(external) == review.EvidenceSha256)
                acceptedExternal.Add(key);
            if (binding is null && !acceptedExternal.Contains(key) && (after?.OpenOrders.Any(x => x.NativeInstrument == order.NativeInstrument && x.ExchangeOrderId == order.ExchangeOrderId) == true ||
                facts[key].Any(x => x.VenueTimestampUtc >= document.Start.FromUtc))) problems.Add(DeribitRecoveryCoherenceProblem.UnknownExternalOrder);
            orders.Add(new(state, latest, binding?.Command.LocalOrderId));
        }
        foreach (var issue in model.Orders.Issues)
            if (issue.Problem != DeribitOrderRecoveryProblem.IncompleteCoverage &&
                !(issue.Problem == DeribitOrderRecoveryProblem.MissingOrderEvidence && terminalProven.Contains((issue.Currency!, issue.ExchangeOrderId!))) &&
                !(issue.Problem == DeribitOrderRecoveryProblem.ExternalOrder && (owned.ContainsKey((issue.Currency!, issue.ExchangeOrderId!)) ||
                    acceptedExternal.Contains((issue.Currency!, issue.ExchangeOrderId!)))))
                problems.Add(DeribitRecoveryCoherenceProblem.InconsistentOrderLedger);
        var resolutions = new List<OmsRecoveryCommandResolution>();
        foreach (var command in document.Ledger.Commands.Where(x => x.Phase != OmsRecoveryDispatchPhase.PersistedNotSent))
        {
            var receipt = receipts.SingleOrDefault(x => x.Command.CommandId == command.Request.CommandId);
            var nativeOrder = receipt is null ? null : orders.SingleOrDefault(x => x.State.Currency == receipt.Order.Currency && x.State.ExchangeOrderId == receipt.Order.ExchangeOrderId);
            if (receipt is null || nativeOrder is null || nativeOrder.LastReport.NativeInstrument != receipt.Order.NativeInstrument ||
                nativeOrder.LastReport.Side != receipt.Order.Side || nativeOrder.LastReport.NativeAmount != receipt.Order.NativeAmount ||
                nativeOrder.LastReport.AmountUnit != receipt.Order.AmountUnit || nativeOrder.LastReport.AmountCurrency != receipt.Order.AmountCurrency ||
                nativeOrder.LastReport.VenueTimestampUtc < receipt.Order.VenueTimestampUtc ||
                nativeOrder.LastReport.Price != receipt.Order.Price || nativeOrder.LastReport.PriceKind != receipt.Order.PriceKind ||
                nativeOrder.LastReport.FilledNativeAmount < receipt.Order.FilledNativeAmount ||
                (receipt.Order.OrderState is "cancelled" or "filled" or "rejected") && nativeOrder.LastReport.OrderState != receipt.Order.OrderState)
                problems.Add(DeribitRecoveryCoherenceProblem.UnresolvedCommand);
            else if (command.Phase is OmsRecoveryDispatchPhase.DispatchStarted or OmsRecoveryDispatchPhase.Ambiguous)
                resolutions.Add(new(command.Request.CommandId, command.Phase));
        }
        var projectedOpen = orders.Where(x => x.State.NativeState is "open" or "triggered" or "untriggered")
            .Select(x => (x.State.NativeInstrument, x.State.ExchangeOrderId)).OrderBy(x => x.NativeInstrument).ThenBy(x => x.ExchangeOrderId).ToArray();
        if (after is null || !projectedOpen.SequenceEqual(after.OpenOrders.Select(x => (x.NativeInstrument, x.ExchangeOrderId)).OrderBy(x => x.NativeInstrument).ThenBy(x => x.ExchangeOrderId)) ||
            after.OpenOrders.Any(x => string.IsNullOrEmpty(x.NativeFingerprint))) problems.Add(DeribitRecoveryCoherenceProblem.InconsistentOrderLedger);
        var stateModel = problems.Count == 0 ? new OmsRecoveredAccountStateV1(1, document.Start.Scope, document.Start.Epoch, document.Start.RunId,
            Policy, now, document.Start.FromUtc, venue!.VenueTimeUtc!.Value, orders.AsReadOnly(), model.Executions, model.Positions,
            model.CurrencyBalances!, document.Ledger.Commands.Where(x => x.Phase == OmsRecoveryDispatchPhase.PersistedNotSent).Select(x => x.Request.CommandId).ToArray(),
            reviews.Where(x => acceptedExternal.Contains((x.Currency, x.ExchangeOrderId))).Select(x => x.DecisionId).ToArray(),
            reviews.Where(x => acceptedExternal.Contains((x.Currency, x.ExchangeOrderId))).Select(x => x.RecordedAtUtc.AddMinutes(10)).Append(now.AddSeconds(5)).Min()) : null;
        return Finish(stateModel, resolutions);

        DeribitRecoveryCoherenceResult Finish(OmsRecoveredAccountStateV1? state, IReadOnlyList<OmsRecoveryCommandResolution>? resolved = null)
        {
            var assessment = new DeribitRecoveryCoherenceAssessment(Policy, now, problems.Order().ToArray());
            var assessedDocument = document with { Coherence = assessment };
            var publication = state is null ? null : new OmsQualifiedRecovery(assessedDocument,
                document.Ledger.EvidenceVersion!.Value, state, resolved ?? [], revision!, time);
            return new(assessment, publication, assessedDocument);
        }
    }
}
