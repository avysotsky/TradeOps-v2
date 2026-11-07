using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public enum DeribitRecoveryCandidateScope { Instrument = 1, InventoryCurrencies, ListedDerivativeSettlementsV2 }

/// <summary>Reproducible provisional model of the archived evidence. Unresolved coverage, absent
/// observations and external orders remain issues. A candidate is never a published account baseline.</summary>
public sealed record DeribitRecoveryCandidateV1(int SchemaVersion, AccountKey Scope, Guid Epoch,
    Guid SnapshotId, DeribitOrderRecoveryResult Orders, IReadOnlyList<OmsPrivateVenueFacts> Executions,
    IReadOnlyList<DeribitAccountPosition> Positions, DeribitCurrencyBalance? Balance,
    IReadOnlyList<DeribitRecoveryEvidenceBlocker> Blockers,
    DeribitRecoveryCandidateScope CoverageScope = DeribitRecoveryCandidateScope.Instrument,
    IReadOnlyList<DeribitCurrencyBalance>? CurrencyBalances = null,
    IReadOnlyList<DeribitRecoveryUniverseIssue>? UniverseIssues = null)
{
    public bool Published => false;
    public bool AccountReady => false;
}

public static class DeribitRecoveryCandidateBuilder
{
    public static DeribitRecoveryCandidateV1? Build(DeribitRecoveryRunDocumentV1 document)
    {
        document.Validate();
        var assessment = document.Assessment; var evidence = assessment.Ledger.Orders;
        // Never turn a malformed/foreign/partial port response into a valid candidate.
        if (evidence.Blockers.Any(x => x is DeribitRecoveryEvidenceBlocker.SnapshotUnusable or
            DeribitRecoveryEvidenceBlocker.SnapshotBindingInvalid or DeribitRecoveryEvidenceBlocker.RetainedEvidenceInvalid or
            DeribitRecoveryEvidenceBlocker.KnownOrderSelectionInvalid or DeribitRecoveryEvidenceBlocker.CommandLedgerInvalid or
            DeribitRecoveryEvidenceBlocker.QueryFailed or DeribitRecoveryEvidenceBlocker.QueryBindingInvalid or
            DeribitRecoveryEvidenceBlocker.QueryWindowInvalid or DeribitRecoveryEvidenceBlocker.QuerySourcesInvalid or
            DeribitRecoveryEvidenceBlocker.QueryEvidenceInvalid or DeribitRecoveryEvidenceBlocker.KnownReadBindingInvalid or
            DeribitRecoveryEvidenceBlocker.KnownReadEvidenceInvalid)) return null;
        var query = document.QueryObservations;
        if (query is null || query.Scope != document.Start.Scope || query.Epoch != document.Start.Epoch ||
            query.NativeInstrument != document.Start.NativeInstrument || query.Currency != document.Start.Currency ||
            !query.TraversalComplete) return null;
        var known = document.KnownOrderObservations;
        if (known is not null && (known.Scope != document.Start.Scope || known.Epoch != document.Start.Epoch ||
            known.SnapshotId != evidence.SnapshotId || !known.QueriesCompleted)) return null;
        var currencies = document.CurrencyObservations ?? [];
        var universe = document.Universe;
        var wide = universe is not null && currencies.Count > 0 &&
            currencies.Count == universe.RequiredCurrencies.Count && currencies.All(x => x.TraversalComplete && x.Kind == "any") &&
            currencies.Select(x => x.Currency).Order(StringComparer.Ordinal).SequenceEqual(universe.RequiredCurrencies.Order(StringComparer.Ordinal));
        OmsPrivateEventEnvelope[] prior;
        try { prior = OmsRecoveryRetainedFacts.Collect(document.Ledger, evidence.AssessedAtUtc)
            .Where(x => wide || x.Facts.NativeInstrument == document.Start.NativeInstrument).ToArray(); }
        catch (ArgumentException) { return null; }
        var current = (wide ? currencies.SelectMany(x => x.Observations) : query.Observations).Concat(known?.Observations ?? []).ToArray();
        if (prior.Length > DeribitOrderRecoveryProjector.MaximumFactsPerSet || current.Length > DeribitOrderRecoveryProjector.MaximumFactsPerSet) return null;
        try
        {
            foreach (var fact in prior.Concat(current))
            {
                fact.Validate();
                if (fact.Scope != document.Start.Scope || !wide && fact.Facts.NativeInstrument != document.Start.NativeInstrument ||
                    !wide && fact.Facts.Currency != document.Start.Currency || fact.Observation.ReceivedAtUtc > evidence.AssessedAtUtc ||
                    fact.Facts.VenueTimestampUtc > evidence.AssessedAtUtc) return null;
            }
            if (current.Any(x => x.Observation.StreamEpoch != document.Start.Epoch ||
                x.Observation.Source != OmsPrivateCaptureSource.RecoveryQuery)) return null;
        }
        catch (ArgumentException) { return null; }
        // No claim that the historical query covers the later account/read/replay interval. The
        // existing reducer records IncompleteCoverage rather than silently extending query bounds.
        var coverage = new DeribitOrderRecoveryCoverage(wide ? currencies.Max(x => x.QueryFromUtc) : query.QueryFromUtc,
            wide ? currencies.Min(x => x.ThroughUtc) : query.ThroughUtc, true,
            wide ? currencies.All(x => x.OrderHistoryComplete) : query.OrderHistoryComplete, true, false);
        var orders = DeribitOrderRecoveryProjector.Rebuild(document.Start.Scope, document.Start.Epoch,
            document.Start.FromUtc, evidence.AssessedAtUtc, coverage, prior, current);
        var fills = new List<OmsPrivateVenueFacts>();
        foreach (var group in prior.Concat(current).Where(x => x.Facts.Kind == OmsJournalEventKind.Execution)
            .GroupBy(x => { var c = OmsPrivateEventCanonical.Encode(x); return (c.Namespace, c.EventId); })
            .OrderBy(x => x.Key.Namespace, StringComparer.Ordinal).ThenBy(x => x.Key.EventId, StringComparer.Ordinal))
        {
            // The reducer records collisions. A conflicting fill never gets an arbitrary "winning" leg.
            if (group.Select(x => OmsPrivateEventCanonical.Encode(x).Json).Distinct(StringComparer.Ordinal).Count() == 1)
                fills.Add(group.First().Facts);
        }
        var account = assessment.Account.ScopedAccountObservationsValidated ? document.AccountObservations : null;
        var positions = account?.Positions.ToArray() ?? [];
        var scopeIssues = (universe?.Issues ?? []).ToList();
        if (!wide && universe is not null)
            foreach (var missing in universe.RequiredCurrencies.Where(c => !currencies.Any(x => x.Currency == c && x.Kind == "any" && x.TraversalComplete)))
                scopeIssues.Add(new(DeribitRecoveryUniverseProblem.CurrencyHistoryUnavailable, Currency: missing));
        IReadOnlyList<DeribitCurrencyBalance>? balances = null;
        if (wide)
        {
            positions = []; var inventory = document.InventoryObservations;
            if (inventory is not null && inventory.IsFreshAt(evidence.AssessedAtUtc) &&
                !scopeIssues.Any(x => x.Problem is DeribitRecoveryUniverseProblem.InvalidInventory or DeribitRecoveryUniverseProblem.InvalidJournal or
                    DeribitRecoveryUniverseProblem.InvalidManifest or DeribitRecoveryUniverseProblem.StaleInventory))
            {
                var normalized = new List<DeribitAccountPosition>();
                var terms = (universe!.InstrumentTerms ?? []).ToDictionary(x => x.NativeInstrument, StringComparer.Ordinal);
                foreach (var native in inventory.Positions)
                {
                    if (!terms.TryGetValue(native.NativeInstrument, out var m) || string.IsNullOrWhiteSpace(m.BaseCurrency) || m.Kind != native.Kind ||
                        native.Kind == "future" && (native.NativeBaseSize is null || m.AmountUnit != OmsPrivateAmountUnit.Usd || m.PositionSizeCurrency != "USD") ||
                        native.Kind == "option" && (m.AmountUnit != OmsPrivateAmountUnit.BaseCoin || m.PositionSizeCurrency != m.BaseCurrency) ||
                        native.NativeBaseSize.HasValue && (native.Direction switch { "buy" => native.NativeBaseSize <= 0,
                            "sell" => native.NativeBaseSize >= 0, "zero" => native.NativeBaseSize != 0, _ => true }))
                    { scopeIssues.Add(new(DeribitRecoveryUniverseProblem.PositionUnitsUnavailable, native.NativeInstrument)); continue; }
                    normalized.Add(new(native.NativeInstrument, native.Kind, native.Direction, native.NativeSize,
                        m.PositionSizeCurrency!, native.Kind == "future" ? native.NativeBaseSize : null, m.BaseCurrency));
                }
                if (!scopeIssues.Any(x => x.Problem == DeribitRecoveryUniverseProblem.PositionUnitsUnavailable)) positions = normalized.ToArray();
                balances = inventory.Balances.ToArray();
                if (account is not null && !scopeIssues.Any(x => x.Problem == DeribitRecoveryUniverseProblem.PositionUnitsUnavailable))
                {
                    var names = account.RequestedInstruments.ToHashSet(StringComparer.Ordinal);
                    var initial = positions.Where(x => names.Contains(x.NativeInstrument)).OrderBy(x => x.NativeInstrument, StringComparer.Ordinal).ToArray();
                    var later = account.Positions.OrderBy(x => x.NativeInstrument, StringComparer.Ordinal).ToArray();
                    if (initial.Length != later.Length || initial.Where((x, i) =>
                        x.NativeInstrument != later[i].NativeInstrument || x.Kind != later[i].Kind || x.Direction != later[i].Direction ||
                        x.NativeSize != later[i].NativeSize || x.SizeCurrency != later[i].SizeCurrency || x.BaseCurrency != later[i].BaseCurrency).Any() ||
                        balances.SingleOrDefault(x => x.Currency == account.Currency) is not { } before ||
                        account.Balance is not { } after || before.CashBalance != after.CashBalance ||
                        before.CrossCollateralEnabled != after.CrossCollateralEnabled || before.PortfolioMarginingEnabled != after.PortfolioMarginingEnabled)
                        scopeIssues.Add(new(DeribitRecoveryUniverseProblem.AccountObservationsChanged, Currency: account.Currency));
                    if (scopeIssues.Any(x => x.Problem == DeribitRecoveryUniverseProblem.AccountObservationsChanged)) { positions = []; balances = null; }
                    else
                    {
                        var latest = later.ToDictionary(x => x.NativeInstrument, StringComparer.Ordinal);
                        positions = positions.Select(x => latest.TryGetValue(x.NativeInstrument, out var observed) ? observed : x).ToArray();
                        balances = balances.Select(x => x.Currency == account.Currency ? account.Balance! : x).ToArray();
                    }
                }
            }
        }
        return new(universe?.HistoryCoverage is null?1:2, document.Start.Scope, document.Start.Epoch, evidence.SnapshotId, orders,
            fills.AsReadOnly(), positions, wide ? null : account?.Balance, evidence.Blockers.ToArray(),
            wide ? universe?.HistoryCoverage is null?DeribitRecoveryCandidateScope.InventoryCurrencies:DeribitRecoveryCandidateScope.ListedDerivativeSettlementsV2 : DeribitRecoveryCandidateScope.Instrument,
            balances, scopeIssues.Distinct().ToArray());
    }
}
