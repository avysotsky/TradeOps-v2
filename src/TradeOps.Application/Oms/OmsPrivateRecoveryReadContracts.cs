using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public enum OmsPrivateRecoveryReadProblem
{
    UnsupportedSchema, InvalidRecord, CapacityExceeded, MissingAcceptedFact,
    OrphanedAcceptedFact, QuarantinedObservation, IntegrityIncident
}

public sealed record OmsPrivateRecoveryObservation(OmsPrivateEventEnvelope Envelope,
    OmsPrivateCaptureResult Disposition);

/// <summary>One bounded, account-scoped DB snapshot, never a resumable commit cursor.</summary>
public sealed record OmsPrivateRecoveryReadResult(AccountKey Scope, Guid SnapshotId,
    DateTimeOffset ReadAtUtc, int AcceptedFactCount,
    IReadOnlyList<OmsPrivateRecoveryObservation> Observations,
    IReadOnlyList<OmsPrivateRecoveryReadProblem> Problems)
{
    public bool IsUsable => Problems.Count == 0;
}

public interface IOmsPrivateRecoveryReader
{
    Task<OmsPrivateRecoveryReadResult> ReadAsync(AccountKey scope, int maximumRows = 10000,
        CancellationToken cancellationToken = default);
}

public sealed record DeribitDurableRecoveryResult(OmsPrivateRecoveryReadResult Evidence,
    DeribitOrderRecoveryResult? Projection);

/// <summary>Read-side composition only; no host readiness transition or trading authority.</summary>
public sealed class DeribitDurableOrderRecovery(IOmsPrivateRecoveryReader reader)
{
    public async Task<DeribitDurableRecoveryResult> RebuildAsync(AccountKey scope, Guid epoch,
        DateTimeOffset requiredFromUtc, DateTimeOffset throughUtc, DeribitOrderRecoveryCoverage coverage,
        CancellationToken cancellationToken = default)
    {
        var evidence = await reader.ReadAsync(scope, cancellationToken: cancellationToken);
        if (evidence.Scope != scope || evidence.SnapshotId == Guid.Empty ||
            evidence.ReadAtUtc.Offset != TimeSpan.Zero || evidence.ReadAtUtc <= DateTimeOffset.UnixEpoch)
            throw new InvalidOperationException("Recovery reader returned an invalid snapshot binding.");
        if (!evidence.IsUsable) return new(evidence, null);
        if (evidence.Observations.Any(x => x.Disposition is not
                (OmsPrivateCaptureResult.Inserted or OmsPrivateCaptureResult.Duplicate)))
            throw new InvalidOperationException("Recovery reader omitted a blocking observation disposition.");
        var prior = evidence.Observations.Where(x => x.Envelope.Observation.StreamEpoch != epoch)
            .Select(x => x.Envelope).ToArray();
        var current = evidence.Observations.Where(x => x.Envelope.Observation.StreamEpoch == epoch)
            .Select(x => x.Envelope).ToArray();
        return new(evidence, DeribitOrderRecoveryProjector.Rebuild(scope, epoch,
            requiredFromUtc, throughUtc, coverage, prior, current));
    }
}
