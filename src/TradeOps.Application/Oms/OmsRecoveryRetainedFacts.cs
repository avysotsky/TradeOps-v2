using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

/// <summary>Facts committed in a previous qualified recovery document, with their original
/// query/capture metadata. This is not an invented inbox insertion or restored live authority.</summary>
public sealed record OmsRetainedRecoveryArchiveV1(int SchemaVersion, AccountKey Scope, Guid SourceRunId,
    long SourceGeneration, long PublishedEvidenceVersion, DateTimeOffset AssessedAtUtc,
    string SourceDocumentSha256, IReadOnlyList<OmsPrivateEventEnvelope> Facts);

public static class OmsRecoveryRetainedFacts
{
    public static IReadOnlyList<OmsPrivateEventEnvelope> Collect(OmsRecoveryLedgerSnapshot snapshot, DateTimeOffset now)
    {
        var archive = snapshot.RecoveryArchive;
        if (archive is not null && (archive.SchemaVersion != 1 || archive.Scope != snapshot.Private.Scope ||
            archive.SourceRunId == Guid.Empty || archive.SourceGeneration <= 0 || archive.PublishedEvidenceVersion < 0 ||
            snapshot.EvidenceVersion is null || archive.PublishedEvidenceVersion > snapshot.EvidenceVersion ||
            archive.AssessedAtUtc.Offset != TimeSpan.Zero || archive.AssessedAtUtc > now ||
            archive.SourceDocumentSha256 is not { Length: 64 } || !archive.SourceDocumentSha256.All(Uri.IsHexDigit) || archive.Facts is null))
            throw new ArgumentException("Invalid retained recovery archive provenance.");
        foreach (var row in snapshot.Private.Observations)
            if (row.Disposition is not (OmsPrivateCaptureResult.Inserted or OmsPrivateCaptureResult.Duplicate) ||
                row.Envelope.Observation.ReceivedAtUtc > snapshot.Private.ReadAtUtc)
                throw new ArgumentException("Invalid retained capture evidence.");
        return Deduplicate(snapshot.Private.Scope, now,
            snapshot.Private.Observations.Select(x => x.Envelope).Concat(archive?.Facts ?? []));
    }

    public static IReadOnlyList<OmsPrivateEventEnvelope> Deduplicate(AccountKey scope, DateTimeOffset through,
        IEnumerable<OmsPrivateEventEnvelope> facts, int maximum = 10000)
    {
        var unique = new Dictionary<(string, string), OmsPrivateEventEnvelope>(); var count = 0;
        foreach (var envelope in facts)
        {
            if (++count > 30000) throw new ArgumentOutOfRangeException(nameof(facts), "Retained input capacity exceeded.");
            envelope.Validate();
            if (envelope.Scope != scope || envelope.Observation.ReceivedAtUtc > through || envelope.Facts.VenueTimestampUtc > through)
                throw new ArgumentException("Retained fact scope/time mismatch.");
            var canonical = OmsPrivateEventCanonical.Encode(envelope); var key = (canonical.Namespace, canonical.EventId);
            if (unique.TryGetValue(key, out var prior))
            {
                // Original v2 decimal scales/hashes remain intact; numerical equality is not a collision.
                if (prior.Facts != envelope.Facts) throw new ArgumentException("Retained native identity collision.");
            }
            else unique.Add(key, envelope);
            if (unique.Count > maximum) throw new ArgumentOutOfRangeException(nameof(facts), "Retained fact capacity exceeded.");
        }
        return Array.AsReadOnly(unique.OrderBy(x => x.Key.Item1, StringComparer.Ordinal).ThenBy(x => x.Key.Item2, StringComparer.Ordinal).Select(x => x.Value).ToArray());
    }
}
