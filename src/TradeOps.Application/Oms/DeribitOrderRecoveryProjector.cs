using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

// Explicitly Deribit-specific: these are v2 native facts, not universal order revisions.
public sealed record DeribitOrderRecoveryCoverage(DateTimeOffset FromUtc, DateTimeOffset ThroughUtc,
    bool OpenOrdersComplete, bool OrderHistoryComplete, bool TradeHistoryComplete,
    bool BufferedReplayComplete, bool GapDetected = false);

public enum DeribitOrderRecoveryProblem
{
    IncompleteCoverage, ScopeMismatch, EpochMismatch, OutsideWindow, ExecutionCollision,
    ExternalOrder, MissingOrderEvidence, TermsChanged, ConflictingOrderReports,
    CumulativeRegression, TerminalReopened, MissingCumulative, FillMismatch, InvalidOrderQuantity,
    QuantityOverflow
}

public sealed record DeribitOrderRecoveryIssue(DeribitOrderRecoveryProblem Problem,
    string? Currency = null, string? ExchangeOrderId = null);

public sealed record DeribitRecoveredOrder(string Currency, string NativeInstrument,
    string ExchangeOrderId, string? NativeState, OrderSide Side, decimal NativeAmount,
    OmsPrivateAmountUnit AmountUnit, string AmountCurrency,
    decimal? KnownFilledNativeAmount, bool PreviouslyObserved, bool HasCurrentEvidence);

public sealed record DeribitOrderRecoveryResult(AccountKey Scope, Guid Epoch,
    DateTimeOffset RequiredFromUtc, DateTimeOffset ThroughUtc, IReadOnlyList<DeribitRecoveredOrder> Orders,
    IReadOnlyList<DeribitOrderRecoveryIssue> Issues, int DistinctExecutions)
{
    // Order-ledger comparison only. This never grants account READY or send permission.
    public bool IsConsistent => Issues.Count == 0;
}

/// <summary>
/// Rebuilds a bounded native order ledger from prior committed facts and a closed recovery batch.
/// Prior facts must include the complete retained execution ledger for the selected orders;
/// recovery queries cover the outage with overlap. Empty open orders never imply cancellation.
/// No I/O, dispatch, database mutation, local-order linking or runtime readiness transition.
/// </summary>
public static class DeribitOrderRecoveryProjector
{
    public const int MaximumFactsPerSet = 10000;

    public static DeribitOrderRecoveryResult Rebuild(AccountKey scope, Guid epoch,
        DateTimeOffset requiredFromUtc, DateTimeOffset throughUtc,
        DeribitOrderRecoveryCoverage coverage,
        IReadOnlyCollection<OmsPrivateEventEnvelope> persisted,
        IReadOnlyCollection<OmsPrivateEventEnvelope> recovered)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(recovered);
        if (scope.Venue.Value != "DERIBIT" ||
            scope.Environment is not (OmsEnvironment.Testnet or OmsEnvironment.Mainnet) || epoch == Guid.Empty)
            throw new ArgumentException("A Deribit recovery scope and epoch are required.");
        OmsPrivateEventCanonical.Utc(requiredFromUtc, nameof(requiredFromUtc));
        OmsPrivateEventCanonical.Utc(throughUtc, nameof(throughUtc));
        OmsPrivateEventCanonical.Utc(coverage.FromUtc, nameof(coverage.FromUtc));
        OmsPrivateEventCanonical.Utc(coverage.ThroughUtc, nameof(coverage.ThroughUtc));
        if (requiredFromUtc > throughUtc || coverage.FromUtc > coverage.ThroughUtc)
            throw new ArgumentException("Recovery windows cannot run backward.");
        if (persisted.Count > MaximumFactsPerSet || recovered.Count > MaximumFactsPerSet)
            throw new ArgumentException("Recovery batch exceeds the bounded projector capacity.");

        var issues = new HashSet<DeribitOrderRecoveryIssue>();
        if (coverage.FromUtc > requiredFromUtc || coverage.ThroughUtc < throughUtc ||
            !coverage.OpenOrdersComplete || !coverage.OrderHistoryComplete || !coverage.TradeHistoryComplete ||
            !coverage.BufferedReplayComplete || coverage.GapDetected)
            issues.Add(new(DeribitOrderRecoveryProblem.IncompleteCoverage));

        var prior = Accept(persisted, false);
        var current = Accept(recovered, true);
        var all = prior.Concat(current).ToArray();
        var known = prior.Select(x => Key(x.Facts)).ToHashSet();
        var executions = new List<OmsPrivateVenueFacts>();
        foreach (var group in all.Where(x => x.Facts.Kind == OmsJournalEventKind.Execution)
                     .GroupBy(x =>
                     {
                         var encoded = OmsPrivateEventCanonical.Encode(x);
                         return (encoded.Namespace, encoded.EventId);
                     }))
        {
            // Compare the complete immutable payload, including price, fee and own order leg.
            if (group.Select(x => OmsPrivateEventCanonical.Encode(x).Json).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                Add(DeribitOrderRecoveryProblem.ExecutionCollision, Key(group.First().Facts));
                continue; // Never choose an arbitrary conflicting execution.
            }
            executions.Add(group.First().Facts);
        }

        var orders = new List<DeribitRecoveredOrder>();
        var currentOrderKeys = current.Where(x => x.Facts.Kind == OmsJournalEventKind.Order)
            .Select(x => Key(x.Facts)).ToHashSet();
        var executionsByOrder = executions.ToLookup(Key);
        foreach (var orderGroup in all.GroupBy(x => Key(x.Facts))
                     .OrderBy(x => x.Key.Currency, StringComparer.Ordinal).ThenBy(x => x.Key.Id, StringComparer.Ordinal))
        {
            var key = orderGroup.Key;
            var facts = orderGroup.Select(x => x.Facts).ToArray();
            var reports = orderGroup.Where(x => x.Facts.Kind == OmsJournalEventKind.Order)
                .OrderBy(x => x.Facts.VenueTimestampUtc)
                .ThenBy(x => OmsPrivateEventCanonical.Encode(x).Json, StringComparer.Ordinal)
                .Select(x => x.Facts).Distinct().ToArray();
            var fresh = currentOrderKeys.Contains(key);
            if (!known.Contains(key)) Add(DeribitOrderRecoveryProblem.ExternalOrder, key);
            if (!fresh) Add(DeribitOrderRecoveryProblem.MissingOrderEvidence, key);
            var terms = facts.Select(x => (x.NativeInstrument, x.Side, x.AmountUnit, x.AmountCurrency)).Distinct().Count();
            if (terms != 1 || reports.Select(x => x.NativeAmount).Distinct().Count() > 1)
                Add(DeribitOrderRecoveryProblem.TermsChanged, key);
            foreach (var sameTime in reports.GroupBy(x => x.VenueTimestampUtc))
                if (sameTime.Distinct().Count() > 1)
                    Add(DeribitOrderRecoveryProblem.ConflictingOrderReports, key);
            decimal? previousFilled = null;
            var terminal = false;
            foreach (var report in reports)
            {
                if (report.FilledNativeAmount < previousFilled) Add(DeribitOrderRecoveryProblem.CumulativeRegression, key);
                if (terminal && !Terminal(report.OrderState)) Add(DeribitOrderRecoveryProblem.TerminalReopened, key);
                previousFilled = report.FilledNativeAmount ?? previousFilled;
                terminal |= Terminal(report.OrderState);
            }

            decimal? filled = 0;
            try { filled = executionsByOrder[key].Sum(x => x.NativeAmount); }
            catch (OverflowException) { filled = null; Add(DeribitOrderRecoveryProblem.QuantityOverflow, key); }
            var latest = reports.LastOrDefault();
            if (latest is not null)
            {
                if (latest.FilledNativeAmount is null) Add(DeribitOrderRecoveryProblem.MissingCumulative, key);
                if (latest.FilledNativeAmount != filled) Add(DeribitOrderRecoveryProblem.FillMismatch, key);
                if (filled > latest.NativeAmount ||
                    (latest.OrderState == "filled" && filled != latest.NativeAmount) ||
                    (latest.OrderState == "rejected" && filled != 0) ||
                    (latest.OrderState == "open" && (filled == latest.NativeAmount ||
                        (latest.LeavesNativeAmount.HasValue && latest.LeavesNativeAmount != latest.NativeAmount - filled))))
                    Add(DeribitOrderRecoveryProblem.InvalidOrderQuantity, key);
            }
            // No report means unknown state/total, never a fabricated cancellation or order size.
            if (latest is not null)
                orders.Add(new(key.Currency, latest.NativeInstrument, key.Id,
                    fresh ? latest.OrderState : null, latest.Side, latest.NativeAmount,
                    latest.AmountUnit, latest.AmountCurrency, filled, known.Contains(key), fresh));
        }
        return new(scope, epoch, requiredFromUtc, throughUtc, orders.AsReadOnly(), issues.OrderBy(x => x.Problem)
            .ThenBy(x => x.Currency, StringComparer.Ordinal).ThenBy(x => x.ExchangeOrderId, StringComparer.Ordinal)
            .ToList().AsReadOnly(), executions.Count);

        List<OmsPrivateEventEnvelope> Accept(IReadOnlyCollection<OmsPrivateEventEnvelope> input, bool isCurrent)
        {
            var accepted = new List<OmsPrivateEventEnvelope>();
            foreach (var item in input)
            {
                item.Validate();
                if (item.Scope != scope) { issues.Add(new(DeribitOrderRecoveryProblem.ScopeMismatch)); continue; }
                if (isCurrent && item.Observation.StreamEpoch != epoch)
                { issues.Add(new(DeribitOrderRecoveryProblem.EpochMismatch)); continue; }
                if (item.Facts.VenueTimestampUtc > throughUtc || item.Observation.ReceivedAtUtc > throughUtc ||
                    (isCurrent && item.Observation.ReceivedAtUtc < requiredFromUtc))
                { issues.Add(new(DeribitOrderRecoveryProblem.OutsideWindow)); continue; }
                accepted.Add(item);
            }
            return accepted;
        }

        void Add(DeribitOrderRecoveryProblem problem, (string Currency, string Id) key) =>
            issues.Add(new(problem, key.Currency, key.Id));
    }

    private static (string Currency, string Id) Key(OmsPrivateVenueFacts facts) => (facts.Currency, facts.ExchangeOrderId);
    private static bool Terminal(string? state) => state is "filled" or "cancelled" or "rejected";
}
