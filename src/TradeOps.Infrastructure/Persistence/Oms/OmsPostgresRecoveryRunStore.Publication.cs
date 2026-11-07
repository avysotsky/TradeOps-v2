using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

public sealed partial class OmsPostgresRecoveryRunStore
{
    public async Task<OmsRecoveryPublication> PublishAsync(OmsRecoveryRunToken token, DeribitRecoveryRunDocumentV1 document,
        OmsQualifiedRecovery qualified, CancellationToken ct = default)
    {
        document.Validate(); var encoded = OmsRecoveryRunCodec.Encode(document);
        if (token.Start != document.Start || qualified.Start != token.Start || !encoded.Sha256.SequenceEqual(qualified.EvidenceSha256))
            throw new ArgumentException("Qualified publication binding mismatch.");
        await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct);
        var state = await LockHead(db, tx, token, ct);
        await using (var prior = Command($"SELECT evidence_version,state_sha256 FROM oms_recovered_account_states_v1 WHERE {Where} AND run_id=@run", db, tx, token.Start.Scope))
        {
            prior.Parameters.AddWithValue("run", token.Start.RunId); await using var rows = await prior.ExecuteReaderAsync(ct);
            if (await rows.ReadAsync(ct))
            {
                if (!rows.GetFieldValue<byte[]>(1).SequenceEqual(qualified.StateSha256)) throw new InvalidOperationException("Publication collision.");
                return new(token, rows.GetInt64(0), Convert.ToHexString(qualified.StateSha256));
            }
        }
        if (state != OmsRecoveryRunState.Reconciling || !qualified.IsCurrent)
            throw new InvalidOperationException("Publication requires current reconciling evidence.");
        await using (var baseline = Command($"""
SELECT p.native_through_utc FROM oms_recovered_account_states_v1 p JOIN oms_recovery_runs_v1 r USING ({Columns},run_id)
WHERE p.tenant_id=@tenant AND p.venue_id=@venue AND p.environment=@environment AND p.account_id=@account AND p.subaccount_id=@subaccount
ORDER BY r.generation DESC LIMIT 1
""", db, tx, token.Start.Scope))
        {
            if (await baseline.ExecuteScalarAsync(ct) is DateTime previous &&
                (token.Start.FromUtc > new DateTimeOffset(previous) || qualified.NativeThroughUtc < new DateTimeOffset(previous)))
                throw new InvalidOperationException("Recovery must cover the interval since the previous published native baseline.");
        }
        // Journal outcome writers lock commands before advancing the scoped evidence version.
        // Keep the same order; locking the counter first would deadlock with a pending outcome.
        await using (var commands = Command($"SELECT command_id FROM oms_commands WHERE {Where} ORDER BY command_id FOR UPDATE", db, tx, token.Start.Scope))
        { await using var rows = await commands.ExecuteReaderAsync(ct); while (await rows.ReadAsync(ct)) { } }
        long version;
        await using (var counter = Command("SELECT public.oms_lock_evidence_version_v1(@tenant,@venue,@environment,@account,@subaccount)", db, tx, token.Start.Scope))
            version = (long)(await counter.ExecuteScalarAsync(ct))!;
        if (version != qualified.ExpectedEvidenceVersion || !qualified.IsCurrent)
            throw new InvalidOperationException("Evidence changed before publication.");
        foreach (var resolution in qualified.CommandResolutions)
        {
            await using var command = Command($"UPDATE oms_commands SET dispatch_phase='Acknowledged',updated_at=clock_timestamp() WHERE {Where} AND command_id=@id AND dispatch_phase=@expected", db, tx, token.Start.Scope);
            command.Parameters.AddWithValue("id", resolution.CommandId.Value); command.Parameters.AddWithValue("expected", resolution.ExpectedPhase.ToString());
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Reconciled command changed.");
            await using var attempt = Command($"UPDATE oms_attempts SET dispatch_phase='Acknowledged',updated_at=clock_timestamp() WHERE {Where} AND command_id=@id AND attempt_number=1 AND dispatch_phase=@expected", db, tx, token.Start.Scope);
            attempt.Parameters.AddWithValue("id", resolution.CommandId.Value); attempt.Parameters.AddWithValue("expected", resolution.ExpectedPhase.ToString());
            if (await attempt.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Reconciled attempt changed.");
            await using var outbox = Command($"UPDATE oms_outbox SET state='Resolved',changed_at=clock_timestamp() WHERE {Where} AND command_id=@id AND state='NeedsRecovery'", db, tx, token.Start.Scope);
            outbox.Parameters.AddWithValue("id", resolution.CommandId.Value);
            if (await outbox.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Reconciled outbox changed.");
        }
        await using (var counter = Command($"SELECT version FROM oms_evidence_versions_v1 WHERE {Where}", db, tx, token.Start.Scope))
            version = (long)(await counter.ExecuteScalarAsync(ct))!;
        await using (var evidence = Command($"INSERT INTO oms_deribit_recovery_results_v1 ({Columns},run_id,schema_version,document_json,document_sha256) VALUES ({Values},@run,1,@json,@sha)", db, tx, token.Start.Scope))
        { evidence.Parameters.AddWithValue("run", token.Start.RunId); AddJson(evidence, encoded); await evidence.ExecuteNonQueryAsync(ct); }
        await using (var projection = Command($"INSERT INTO oms_recovered_account_states_v1 ({Columns},run_id,evidence_version,native_through_utc,schema_version,state_json,state_sha256) VALUES ({Values},@run,@version,@through,1,@json,@sha)", db, tx, token.Start.Scope))
        {
            projection.Parameters.AddWithValue("run", token.Start.RunId); projection.Parameters.AddWithValue("version", version);
            projection.Parameters.AddWithValue("through", qualified.NativeThroughUtc.UtcDateTime);
            AddJson(projection, (qualified.StateJson, qualified.StateSha256)); await projection.ExecuteNonQueryAsync(ct);
        }
        await Update(db, tx, token, OmsRecoveryRunState.Ready, "QualifiedPublication", ct);
        if (!qualified.IsCurrent) throw new InvalidOperationException("Capture changed during publication.");
        await tx.CommitAsync(ct);
        // A notification concurrent with COMMIT is denied by the runtime gate; persisted READY alone is never authority.
        return new(token, version, Convert.ToHexString(qualified.StateSha256));
    }

    public async Task<OmsRecoveredAccountArchive?> ReadPublishedAsync(AccountKey scope, CancellationToken ct = default)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var query = Command($"""
SELECT r.start_json::text,r.start_sha256,r.generation,p.evidence_version,p.schema_version,
 CASE WHEN octet_length(p.state_json::text)<=@capacity THEN p.state_json::text END,p.state_sha256
FROM oms_recovered_account_states_v1 p JOIN oms_recovery_runs_v1 r USING ({Columns},run_id)
WHERE p.tenant_id=@tenant AND p.venue_id=@venue AND p.environment=@environment AND p.account_id=@account AND p.subaccount_id=@subaccount
ORDER BY r.generation DESC LIMIT 1
""", db, null, scope);
        query.Parameters.AddWithValue("capacity", OmsRecoveryRunCodec.MaximumDocumentBytes);
        await using var rows = await query.ExecuteReaderAsync(ct); if (!await rows.ReadAsync(ct)) return null;
        var start = OmsRecoveryRunCodec.Decode<OmsRecoveryRunStart>(rows.GetString(0), rows.GetFieldValue<byte[]>(1)); start.Validate();
        if (start.Scope != scope || rows.GetInt32(4) != 1 || rows.IsDBNull(5)) throw new InvalidOperationException("Invalid recovered state binding or bounds.");
        var digest = rows.GetFieldValue<byte[]>(6); var recovered = OmsRecoveryRunCodec.Decode<OmsRecoveredAccountStateV1>(rows.GetString(5), digest);
        if (recovered.SchemaVersion != 1 || recovered.Scope != scope || recovered.Epoch != start.Epoch || recovered.RunId != start.RunId ||
            !DeribitRecoveryCoherencePolicy.IsSupportedPolicy(recovered.Policy)) throw new InvalidOperationException("Invalid recovered state identity.");
        return new(new(new(start, rows.GetInt64(2)), rows.GetInt64(3), Convert.ToHexString(digest)), recovered);
    }

    public async Task<bool> IsPublishedCurrentAsync(OmsRecoveryPublication publication, CancellationToken ct = default)
    {
        var token = publication.Token; token.Start.Validate();
        if (publication.EvidenceVersion < 0 || publication.StateSha256.Length != 64 || !publication.StateSha256.All(Uri.IsHexDigit)) return false;
        await using var db = await source.OpenConnectionAsync(ct);
        await using var query = Command($"""
SELECT EXISTS (
 SELECT 1 FROM oms_recovery_heads_v1 h JOIN oms_recovery_runs_v1 r USING ({Columns},run_id)
 JOIN oms_recovered_account_states_v1 p USING ({Columns},run_id)
 JOIN oms_evidence_versions_v1 v USING ({Columns})
 WHERE h.tenant_id=@tenant AND h.venue_id=@venue AND h.environment=@environment AND h.account_id=@account AND h.subaccount_id=@subaccount
 AND h.run_id=@run AND r.epoch=@epoch AND r.generation=@generation AND h.state='Ready'
 AND r.start_sha256=@start AND p.evidence_version=@version AND v.version=p.evidence_version AND p.state_sha256=@sha)
""", db, null, token.Start.Scope);
        query.Parameters.AddWithValue("run", token.Start.RunId); query.Parameters.AddWithValue("epoch", token.Start.Epoch);
        query.Parameters.AddWithValue("generation", token.Generation); query.Parameters.AddWithValue("version", publication.EvidenceVersion);
        query.Parameters.AddWithValue("start", OmsRecoveryRunCodec.Encode(token.Start).Sha256);
        query.Parameters.AddWithValue("sha", Convert.FromHexString(publication.StateSha256));
        return await query.ExecuteScalarAsync(ct) is true;
    }
}
