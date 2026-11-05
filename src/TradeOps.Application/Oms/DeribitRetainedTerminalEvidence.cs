using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public enum DeribitTerminalEvidenceProblem
{
    SnapshotInvalid, BindingInvalid, MissingTerminalReport, MissingCumulative,
    TermsMismatch, ConflictingReports, ExecutionCollision, ExecutionAfterTerminal,
    LedgerMismatch, InvalidTerminalQuantity, QuantityOverflow, MissingExecutionDetails
}
public sealed record DeribitRetainedTerminalOrderEvidence(string Currency, string ExchangeOrderId,
    bool IsSufficient, int DistinctExecutions, IReadOnlyList<DeribitTerminalEvidenceProblem> Problems);
public sealed record DeribitRetainedTerminalAssessment(AccountKey Scope, Guid Epoch, Guid SnapshotId,
    IReadOnlyList<DeribitRetainedTerminalOrderEvidence> Orders);

/// <summary>Historical terminal-report/ledger sufficiency, not fresh venue state or account/history completeness.</summary>
public static class DeribitRetainedTerminalEvidence
{
    public static DeribitRetainedTerminalAssessment Assess(OmsPrivateRecoveryReadResult snapshot,
        DeribitKnownOrderReadRequest known, DeribitRecoveryQueryResult current, DateTimeOffset assessedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(known); ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(known.Scope);
        if (known.Orders is null || known.Orders.Count is < 1 or > 32 || known.Orders.Any(x => x is null))
            throw new ArgumentException("Bounded known orders required.");
        var seeds = known.Orders.ToArray(); var global = new HashSet<DeribitTerminalEvidenceProblem>();
        var prior = new Dictionary<(string, string), OmsPrivateEventEnvelope>();
        var all = new Dictionary<(string, string), OmsPrivateEventEnvelope>();
        var sources = new[] { "open", "orders-recent", "orders-historical", "trades-recent", "trades-historical" };
        if (!Utc(assessedAtUtc) || known.Scope != snapshot.Scope || known.Scope != current.Scope ||
            known.Epoch == Guid.Empty || known.Epoch != current.Epoch || known.SnapshotId == Guid.Empty || known.SnapshotId != snapshot.SnapshotId ||
            seeds.Select(x => (x.Currency, x.ExchangeOrderId)).Distinct().Count() != seeds.Length ||
            !Utc(current.QueryFromUtc) || !Utc(current.ThroughUtc) || current.QueryFromUtc > current.ThroughUtc ||
            !Utc(current.StartedAtUtc) || !Utc(current.CompletedAtUtc) || current.StartedAtUtc < snapshot.ReadAtUtc ||
            current.CompletedAtUtc < current.StartedAtUtc || current.CompletedAtUtc > assessedAtUtc || current.ThroughUtc > current.StartedAtUtc ||
            current.Problems is null || current.Problems.Count != 0 || current.Observations is null || current.Observations.Count > 10000 ||
            current.Sources is null || current.Sources.Count != 5 || current.Sources.Any(x => x is null) ||
            !current.Sources.Select(x => x.Source).Order(StringComparer.Ordinal).SequenceEqual(sources.Order(StringComparer.Ordinal)) ||
            current.Sources.Any(x => !x.Exhausted || x.Pages is < 1 or > 20))
            global.Add(DeribitTerminalEvidenceProblem.BindingInvalid);
        if (snapshot.Problems is null || snapshot.Problems.Count != 0 || !Utc(snapshot.ReadAtUtc) || snapshot.ReadAtUtc > assessedAtUtc ||
            snapshot.Observations is null || snapshot.Observations.Count > 10000 || snapshot.AcceptedFactCount is < 0 or > 10000)
            global.Add(DeribitTerminalEvidenceProblem.SnapshotInvalid);
        if (global.Count == 0)
        {
            foreach (var row in snapshot.Observations!)
            {
                if (row is null || row.Disposition is not (OmsPrivateCaptureResult.Inserted or OmsPrivateCaptureResult.Duplicate) ||
                    !Valid(row.Envelope, snapshot.ReadAtUtc, false))
                { global.Add(DeribitTerminalEvidenceProblem.SnapshotInvalid); break; }
                Add(row.Envelope, prior); Add(row.Envelope, all);
            }
            if (prior.Count != snapshot.AcceptedFactCount) global.Add(DeribitTerminalEvidenceProblem.SnapshotInvalid);
            foreach (var envelope in current.Observations!)
            {
                if (!Valid(envelope, current.CompletedAtUtc, true) || envelope.Observation.ReceivedAtUtc < current.StartedAtUtc ||
                    envelope.Facts.NativeInstrument != current.NativeInstrument || envelope.Facts.Currency != current.Currency ||
                    (envelope.Observation.Channel == "private/get_user_trades_by_instrument"
                        ? envelope.Facts.Kind != OmsJournalEventKind.Execution || envelope.Facts.VenueTimestampUtc < current.QueryFromUtc || envelope.Facts.VenueTimestampUtc > current.ThroughUtc
                        : envelope.Facts.Kind != OmsJournalEventKind.Order || envelope.Observation.Channel is not
                            ("private/get_open_orders_by_instrument" or "private/get_order_history_by_instrument") ||
                            envelope.Observation.Channel == "private/get_order_history_by_instrument" &&
                                (envelope.Facts.VenueTimestampUtc < current.QueryFromUtc || envelope.Facts.VenueTimestampUtc > current.ThroughUtc) ||
                            envelope.Observation.Channel == "private/get_open_orders_by_instrument" && envelope.Facts.OrderState is not ("open" or "triggered" or "untriggered")))
                { global.Add(DeribitTerminalEvidenceProblem.BindingInvalid); break; }
                Add(envelope, all);
            }
        }
        var priorByOrder = prior.Values.ToLookup(x => (x.Facts.Currency, x.Facts.ExchangeOrderId));
        var allByOrder = all.Values.ToLookup(x => (x.Facts.Currency, x.Facts.ExchangeOrderId));
        var results = new List<DeribitRetainedTerminalOrderEvidence>();
        foreach (var seed in seeds)
        {
            var problems = global.ToHashSet(); var count = 0;
            try { seed.Validate(); }
            catch (ArgumentException) { problems.Add(DeribitTerminalEvidenceProblem.BindingInvalid); }
            if (problems.Count == 0)
            {
                var key = (seed.Currency, seed.ExchangeOrderId);
                var storedReports = priorByOrder[key].Where(x => x.Facts.Kind == OmsJournalEventKind.Order)
                    .Select(x => x.Facts).OrderBy(x => x.VenueTimestampUtc).ToArray();
                if (seed.Kind != OmsJournalEventKind.Order || !Terminal(seed.OrderState) || seed.NativeInstrument != current.NativeInstrument ||
                    seed.Currency != current.Currency || seed.VenueTimestampUtc > current.ThroughUtc || storedReports.Length == 0 ||
                    storedReports[^1].VenueTimestampUtc != seed.VenueTimestampUtc || !storedReports.Contains(seed))
                    problems.Add(DeribitTerminalEvidenceProblem.MissingTerminalReport);
                var facts = allByOrder[key].Select(x => x.Facts).ToArray();
                var reports = facts.Where(x => x.Kind == OmsJournalEventKind.Order).OrderBy(x => x.VenueTimestampUtc).ToArray();
                if (facts.Any(x => x.NativeInstrument != seed.NativeInstrument || x.Side != seed.Side || x.AmountUnit != seed.AmountUnit || x.AmountCurrency != seed.AmountCurrency) ||
                    reports.Any(x => x.NativeAmount != seed.NativeAmount)) problems.Add(DeribitTerminalEvidenceProblem.TermsMismatch);
                if (reports.GroupBy(x => x.VenueTimestampUtc).Any(x => x.Count() > 1)) problems.Add(DeribitTerminalEvidenceProblem.ConflictingReports);
                decimal? cumulative = null; OmsPrivateVenueFacts? terminal = null;
                foreach (var report in reports)
                {
                    if (!report.FilledNativeAmount.HasValue) problems.Add(DeribitTerminalEvidenceProblem.MissingCumulative);
                    if (report.FilledNativeAmount < cumulative || terminal is not null &&
                        (report.OrderState != terminal.OrderState || report.FilledNativeAmount != terminal.FilledNativeAmount))
                        problems.Add(DeribitTerminalEvidenceProblem.ConflictingReports);
                    if (report.OrderState == "filled" && report.FilledNativeAmount != report.NativeAmount ||
                        report.OrderState == "rejected" && report.FilledNativeAmount != 0)
                        problems.Add(DeribitTerminalEvidenceProblem.InvalidTerminalQuantity);
                    cumulative = report.FilledNativeAmount ?? cumulative;
                    if (Terminal(report.OrderState)) terminal ??= report;
                }
                var executions = facts.Where(x => x.Kind == OmsJournalEventKind.Execution).ToArray(); count = executions.Length;
                if (executions.Any(x => x.Fee is null || x.FeeCurrency is null)) problems.Add(DeribitTerminalEvidenceProblem.MissingExecutionDetails);
                if (facts.Any(x => x.VenueTimestampUtc > seed.VenueTimestampUtc)) problems.Add(DeribitTerminalEvidenceProblem.ExecutionAfterTerminal);
                decimal total = 0;
                try { foreach (var execution in executions) total = checked(total+execution.NativeAmount); }
                catch (OverflowException) { problems.Add(DeribitTerminalEvidenceProblem.QuantityOverflow); }
                if (!seed.FilledNativeAmount.HasValue) problems.Add(DeribitTerminalEvidenceProblem.MissingCumulative);
                else if (total != seed.FilledNativeAmount.Value) problems.Add(DeribitTerminalEvidenceProblem.LedgerMismatch);
            }
            results.Add(new(seed.Currency, seed.ExchangeOrderId, problems.Count == 0, count, Array.AsReadOnly(problems.Order().ToArray())));
        }
        return new(known.Scope, known.Epoch, known.SnapshotId, results.AsReadOnly());

        bool Valid(OmsPrivateEventEnvelope? envelope, DateTimeOffset receivedThrough, bool query)
        {
            if (envelope is null) return false;
            try { envelope.Validate(); }
            catch (ArgumentException) { return false; }
            return envelope.Scope == known.Scope && envelope.Facts.VenueTimestampUtc <= envelope.Observation.ReceivedAtUtc &&
                envelope.Observation.ReceivedAtUtc <= receivedThrough && (!query || envelope.Observation.StreamEpoch == known.Epoch &&
                envelope.Observation.Source == OmsPrivateCaptureSource.RecoveryQuery);
        }
        void Add(OmsPrivateEventEnvelope envelope, Dictionary<(string, string), OmsPrivateEventEnvelope> target)
        {
            var encoded = OmsPrivateEventCanonical.Encode(envelope); var key = (encoded.Namespace, encoded.EventId);
            if (target.TryGetValue(key, out var previous) && !OmsPrivateEventCanonical.Encode(previous).Sha256.SequenceEqual(encoded.Sha256))
                global.Add(DeribitTerminalEvidenceProblem.ExecutionCollision);
            else target[key] = envelope;
        }
    }
    private static bool Utc(DateTimeOffset value) => value.Offset == TimeSpan.Zero && value > DateTimeOffset.UnixEpoch;
    private static bool Terminal(string? state) => state is "cancelled" or "filled" or "rejected";
}
