using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

/// <summary>Reproducible provisional model of the archived evidence. Unresolved coverage, absent
/// observations and external orders remain issues. A candidate is never a published account baseline.</summary>
public sealed record DeribitRecoveryCandidateV1(int SchemaVersion, AccountKey Scope, Guid Epoch,
    Guid SnapshotId, DeribitOrderRecoveryResult Orders, IReadOnlyList<OmsPrivateVenueFacts> Executions,
    IReadOnlyList<DeribitAccountPosition> Positions, DeribitCurrencyBalance? Balance,
    IReadOnlyList<DeribitRecoveryEvidenceBlocker> Blockers)
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
        var prior = document.Ledger.Private.Observations.Select(x => x.Envelope)
            .Where(x => x.Facts.NativeInstrument == document.Start.NativeInstrument).ToArray();
        var current = query.Observations.Concat(known?.Observations ?? []).ToArray();
        if (prior.Length > DeribitOrderRecoveryProjector.MaximumFactsPerSet || current.Length > DeribitOrderRecoveryProjector.MaximumFactsPerSet) return null;
        try
        {
            foreach (var fact in prior.Concat(current))
            {
                fact.Validate();
                if (fact.Scope != document.Start.Scope || fact.Facts.NativeInstrument != document.Start.NativeInstrument ||
                    fact.Facts.Currency != document.Start.Currency || fact.Observation.ReceivedAtUtc > evidence.AssessedAtUtc ||
                    fact.Facts.VenueTimestampUtc > evidence.AssessedAtUtc) return null;
            }
            if (current.Any(x => x.Observation.StreamEpoch != document.Start.Epoch ||
                x.Observation.Source != OmsPrivateCaptureSource.RecoveryQuery)) return null;
        }
        catch (ArgumentException) { return null; }
        // No claim that the historical query covers the later account/read/replay interval. The
        // existing reducer records IncompleteCoverage rather than silently extending query bounds.
        var coverage = new DeribitOrderRecoveryCoverage(query.QueryFromUtc, query.ThroughUtc,
            true, query.OrderHistoryComplete, true, false);
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
        return new(1, document.Start.Scope, document.Start.Epoch, evidence.SnapshotId, orders,
            fills.AsReadOnly(), account?.Positions.ToArray() ?? [], account?.Balance, evidence.Blockers.ToArray());
    }
}
