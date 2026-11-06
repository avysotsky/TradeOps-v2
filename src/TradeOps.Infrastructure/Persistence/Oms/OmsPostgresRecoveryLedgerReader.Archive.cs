using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

public sealed partial class OmsPostgresRecoveryLedgerReader
{
    private static async Task<OmsRetainedRecoveryArchiveV1?> ReadArchiveAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        AccountKey scope, int maximumRows, long version, CancellationToken ct)
    {
        await using var query = new NpgsqlCommand($"""
SELECT c.run_id,r.generation,c.evidence_version,d.schema_version,
 CASE WHEN octet_length(d.document_json::text)<=@capacity THEN d.document_json::text END,d.document_sha256,c.native_through_utc
FROM oms_recovered_account_states_v1 c JOIN oms_recovery_runs_v1 r
 USING (tenant_id,venue_id,environment,account_id,subaccount_id,run_id)
JOIN oms_deribit_recovery_results_v1 d USING (tenant_id,venue_id,environment,account_id,subaccount_id,run_id)
WHERE {ScopeWhere} ORDER BY r.generation DESC LIMIT 1
""", db, tx);
        query.Parameters.AddWithValue("tenant", scope.Tenant.Value); query.Parameters.AddWithValue("venue", scope.Venue.Value);
        query.Parameters.AddWithValue("environment", (short)scope.Environment); query.Parameters.AddWithValue("account", scope.Account.Value);
        query.Parameters.AddWithValue("subaccount", scope.Subaccount?.Value ?? ""); query.Parameters.AddWithValue("capacity", OmsRecoveryRunCodec.MaximumDocumentBytes);
        await using var rows = await query.ExecuteReaderAsync(ct); if (!await rows.ReadAsync(ct)) return null;
        if (rows.IsDBNull(4)) throw new ArgumentOutOfRangeException(nameof(maximumRows), "Retained document capacity exceeded.");
        var digest = rows.GetFieldValue<byte[]>(5);
        var document = OmsRecoveryRunCodec.Decode<DeribitRecoveryRunDocumentV1>(rows.GetString(4), digest); document.Validate();
        if (rows.GetInt32(3) != 1 || document.Start.Scope != scope || document.Start.RunId != rows.GetGuid(0) || rows.GetInt64(1) <= 0 ||
            document.Coherence is not { EvidenceSufficient: true } coherence || document.Candidate?.CoverageScope != DeribitRecoveryCandidateScope.InventoryCurrencies ||
            document.VenueClock?.VenueTimeUtc != new DateTimeOffset(rows.GetDateTime(6)) ||
            document.Ledger.EvidenceVersion is not >= 0 || document.Ledger.EvidenceVersion > rows.GetInt64(2) || rows.GetInt64(2) > version ||
            document.Ledger.RecoveryArchive is { } prior && (prior.SourceGeneration >= rows.GetInt64(1) || prior.SourceRunId == document.Start.RunId))
            throw new ArgumentException("Invalid retained publication provenance.");
        // The immutable committed document is the checkpoint for recovery-query facts. Do not
        // relabel them as WebSocket inbox insertions or discard them when the next query advances.
        _ = OmsRecoveryRetainedFacts.Collect(document.Ledger, coherence.AssessedAtUtc);
        var observations = (document.Ledger.RecoveryArchive?.Facts ?? [])
            .Concat((document.CurrencyObservations ?? []).SelectMany(x => x.Observations))
            .Concat(document.KnownOrderObservations?.Observations ?? []);
        var facts = OmsRecoveryRetainedFacts.Deduplicate(scope, coherence.AssessedAtUtc, observations, maximumRows);
        if (facts.Any(x => x.Observation.Source != OmsPrivateCaptureSource.RecoveryQuery))
            throw new ArgumentException("Retained query provenance required.");
        return new(1, scope, document.Start.RunId, rows.GetInt64(1), rows.GetInt64(2), coherence.AssessedAtUtc,
            Convert.ToHexString(digest), facts);
    }
}
