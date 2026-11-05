using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public sealed record DeribitKnownOrderReadRequest(AccountKey Scope, Guid Epoch, Guid SnapshotId,
    IReadOnlyList<OmsPrivateVenueFacts> Orders);
public enum DeribitKnownOrderLookupOutcome { Found, Missing, Unsupported }
public sealed record DeribitKnownOrderLookup(int RequestIndex, DeribitKnownOrderLookupOutcome Outcome, int? VenueCode = null);
public enum DeribitKnownOrderReadProblem { Authentication, AccountMismatch, Protocol, Transport, Deadline, Capacity, EvidenceMismatch }
public sealed record DeribitKnownOrderReadResult(AccountKey Scope, Guid Epoch, Guid SnapshotId,
    DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, int RequestedOrders,
    IReadOnlyList<DeribitKnownOrderLookup> Lookups, IReadOnlyList<OmsPrivateEventEnvelope> Observations,
    IReadOnlyList<DeribitKnownOrderReadProblem> Problems, int? HttpStatusCode = null)
{
    public bool QueriesCompleted => Problems.Count == 0 && Lookups.Count == RequestedOrders;
    public bool AllOrdersFound => QueriesCompleted && RequestedOrders > 0 && Lookups.All(x => x.Outcome == DeribitKnownOrderLookupOutcome.Found);
    public bool CoherentRecovery => false;
}
public interface IDeribitKnownOrderReader
{
    Task<DeribitKnownOrderReadResult> ReadAsync(DeribitKnownOrderReadRequest request, CancellationToken ct = default);
}

/// <summary>Select references only from an integrity-checked, fully scoped retained snapshot. Never infer an order from absence.</summary>
public static class DeribitKnownOrderSelection
{
    public static DeribitKnownOrderReadRequest FromSnapshot(OmsPrivateRecoveryReadResult snapshot, AccountKey scope,
        Guid epoch, string instrument)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsUsable || snapshot.Scope != scope || snapshot.SnapshotId == Guid.Empty || epoch == Guid.Empty ||
            snapshot.ReadAtUtc.Offset != TimeSpan.Zero || snapshot.ReadAtUtc <= DateTimeOffset.UnixEpoch ||
            snapshot.Observations.Count > 10000 || snapshot.Observations.Any(x =>
                x.Disposition is not (OmsPrivateCaptureResult.Inserted or OmsPrivateCaptureResult.Duplicate)))
            throw new InvalidOperationException("Retained known-order snapshot refused.");
        foreach (var row in snapshot.Observations)
        {
            row.Envelope.Validate();
            if (row.Envelope.Scope != scope || row.Envelope.Facts.VenueTimestampUtc > snapshot.ReadAtUtc)
                throw new InvalidOperationException("Retained scope/time refused.");
        }
        var facts = snapshot.Observations.Select(x => x.Envelope.Facts).Where(x => x.NativeInstrument == instrument).ToArray();
        var seeds = new List<OmsPrivateVenueFacts>();
        foreach (var group in facts.GroupBy(x => (x.Currency, x.ExchangeOrderId)).OrderBy(x => x.Key.Currency, StringComparer.Ordinal)
                     .ThenBy(x => x.Key.ExchangeOrderId, StringComparer.Ordinal))
        {
            var reports = group.Where(x => x.Kind == OmsJournalEventKind.Order).OrderBy(x => x.VenueTimestampUtc).Distinct().ToArray();
            if (reports.Length == 0 || reports.Select(x => (x.Side, x.NativeAmount, x.AmountUnit, x.AmountCurrency)).Distinct().Count() != 1 ||
                reports.GroupBy(x => x.VenueTimestampUtc).Any(x => x.Distinct().Count() > 1))
                throw new InvalidOperationException("Retained order terms refused.");
            decimal? filled = null; var terminal = false;
            foreach (var report in reports)
            {
                var nowTerminal = report.OrderState is "cancelled" or "filled" or "rejected";
                if (report.FilledNativeAmount < filled || terminal && !nowTerminal)
                    throw new InvalidOperationException("Retained order progression refused.");
                filled = report.FilledNativeAmount ?? filled; terminal |= nowTerminal;
            }
            seeds.Add(reports[^1]);
        }
        if (seeds.Count is < 1 or > 32) throw new InvalidOperationException("Bounded nonempty known-order set required.");
        return new(scope, epoch, snapshot.SnapshotId, seeds.AsReadOnly());
    }
}
