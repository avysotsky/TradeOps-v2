using Npgsql;
using NpgsqlTypes;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>Store-only admission. No transport authority, DI default or live policy is installed.
/// Qualified recovery is a live process capability; an archived DTO cannot replace it.</summary>
public sealed class OmsPostgresRiskAdmission(NpgsqlDataSource source, IOmsRiskInputsReader inputs,
    TimeProvider? clock = null, IOmsRiskPolicyReader? cancelPolicies = null)
{
    private const string Where = "tenant_id=@tenant AND venue_id=@venue AND environment=@environment AND account_id=@account AND subaccount_id=@subaccount";
    private static NpgsqlCommand Query(NpgsqlConnection db, NpgsqlTransaction? tx, AccountKey scope, string sql)
    {
        var q = new NpgsqlCommand(sql, db, tx);
        q.Parameters.AddWithValue("tenant", scope.Tenant.Value); q.Parameters.AddWithValue("venue", scope.Venue.Value);
        q.Parameters.AddWithValue("environment", (short)scope.Environment); q.Parameters.AddWithValue("account", scope.Account.Value);
        q.Parameters.AddWithValue("subaccount", scope.Subaccount?.Value ?? ""); return q;
    }

    public async Task<OmsRiskAdmissionResult> AdmitAsync(OmsJournalCommandRequest command,
        OmsRecoveryPublication publication, OmsQualifiedRecovery qualified, CancellationToken ct = default)
    {
        command.Validate(); var encoded = OmsJournalCanonical.EncodeCommand(command);
        if (qualified.Start != publication.Token.Start || qualified.Start.Scope != command.Scope ||
            publication.StateSha256 != Convert.ToHexString(qualified.StateSha256) ||
            publication.EvidenceVersion < qualified.ExpectedEvidenceVersion || !qualified.IsCurrent)
        {
            await using var rejected = await source.OpenConnectionAsync(ct);
            await AuditAsync(rejected, null, command, publication, qualified, OmsRiskAdmissionResult.StaleBaseline, ct);
            return OmsRiskAdmissionResult.StaleBaseline;
        }
        await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct);
        async Task<OmsRiskAdmissionResult> Reject(OmsRiskAdmissionResult result)
        {
            // CAS may have tentatively changed aggregates. Roll everything back before recording a denial.
            await tx.RollbackAsync(ct);
            await AuditAsync(db, null, command, publication, qualified, result, ct);
            return result;
        }
        var controls = await OmsPostgresRiskControls.LockAndReadAsync(db, tx, command.Scope, ct);
        if (controls.Blocked && command.Payload.Kind!=OmsJournalCommandKind.Cancel) return await Reject(OmsRiskAdmissionResult.ControlBlocked);
        await OmsPostgresJournal.LockIdAsync(db, tx, command.Scope, "Command", command.CommandId.Value, ct);
        await OmsPostgresJournal.LockIdAsync(db, tx, command.Scope, "Intent", command.IntentId.Value, ct);
        await using (var duplicate = Query(db, tx, command.Scope, $"SELECT command_sha256 FROM oms_risk_reservations_v1 WHERE {Where} AND command_id=@command UNION ALL SELECT command_sha256 FROM oms_risk_cancel_admissions_v1 WHERE {Where} AND command_id=@command"))
        {
            duplicate.Parameters.AddWithValue("command", command.CommandId.Value);
            if (await duplicate.ExecuteScalarAsync(ct) is byte[] hash)
                return await Reject(hash.SequenceEqual(encoded.Sha256) ? OmsRiskAdmissionResult.Duplicate : OmsRiskAdmissionResult.Collision);
        }
        // Same account head lock as recovery publication. Account admissions are serialized before summing reservations.
        await using (var head = Query(db, tx, command.Scope, "SELECT public.oms_lock_risk_recovery_head_v1(@tenant,@venue,@environment,@account,@subaccount)"))
            if (await head.ExecuteScalarAsync(ct) is not Guid run || run != publication.Token.Start.RunId)
                return await Reject(OmsRiskAdmissionResult.StaleBaseline);
        await using (var rows = Query(db, tx, command.Scope, $"SELECT command_id FROM oms_commands WHERE {Where} ORDER BY command_id FOR UPDATE"))
        { await using var r = await rows.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) { } }
        long before;
        await using (var counter = Query(db, tx, command.Scope, "SELECT public.oms_lock_evidence_version_v1(@tenant,@venue,@environment,@account,@subaccount)"))
            before = (long)(await counter.ExecuteScalarAsync(ct))!;
        // Only our own atomic admissions may extend the baseline version. Any other journal change requires recovery.
        long expected = publication.EvidenceVersion;
        await using (var chain = Query(db, tx, command.Scope, $"SELECT max(evidence_after) FROM (SELECT evidence_after FROM oms_risk_reservations_v1 WHERE {Where} AND run_id=@run AND generation=@generation AND state_sha256=@sha UNION ALL SELECT evidence_after FROM oms_risk_cancel_admissions_v1 WHERE {Where} AND run_id=@run AND generation=@generation AND state_sha256=@sha) chain"))
        {
            chain.Parameters.AddWithValue("run", publication.Token.Start.RunId); chain.Parameters.AddWithValue("generation", publication.Token.Generation);
            chain.Parameters.AddWithValue("sha", qualified.StateSha256);
            if (await chain.ExecuteScalarAsync(ct) is long last) expected = last;
        }
        await using (var binding = Query(db, tx, command.Scope, $"""
SELECT EXISTS(SELECT 1 FROM oms_recovered_account_states_v1 p JOIN oms_recovery_runs_v1 r
 USING(tenant_id,venue_id,environment,account_id,subaccount_id,run_id)
 WHERE p.tenant_id=@tenant AND p.venue_id=@venue AND p.environment=@environment AND p.account_id=@account AND p.subaccount_id=@subaccount
 AND p.run_id=@run AND r.generation=@generation AND p.state_sha256=@sha AND p.evidence_version=@version)
"""))
        {
            binding.Parameters.AddWithValue("run", publication.Token.Start.RunId); binding.Parameters.AddWithValue("generation", publication.Token.Generation);
            binding.Parameters.AddWithValue("sha", qualified.StateSha256); binding.Parameters.AddWithValue("version", publication.EvidenceVersion);
            if (before != expected || await binding.ExecuteScalarAsync(ct) is not true || !qualified.IsCurrent)
                return await Reject(OmsRiskAdmissionResult.StaleBaseline);
        }
        var baseline = OmsRecoveryRunCodec.Decode<OmsRecoveredAccountStateV1>(qualified.StateJson, qualified.StateSha256);
        if(command.Payload.Kind==OmsJournalCommandKind.Cancel)
        {
            var parent=await ReadCancelParentAsync(db,tx,command,ct);
            var policy=cancelPolicies is null ? null : await cancelPolicies.ReadAsync(command.Scope,ct);
            if(parent is null||!OmsAddressedCancelPolicyV1.Allows(command,parent,baseline,policy,(clock??TimeProvider.System).GetUtcNow()))
                return await Reject(OmsRiskAdmissionResult.Denied);
            var writeCancel=await OmsPostgresJournal.PersistInTransactionAsync(db,tx,command,null,ct);
            if(writeCancel!=OmsJournalWriteResult.Created)return await Reject(writeCancel==OmsJournalWriteResult.StaleVersion?OmsRiskAdmissionResult.StaleOrderVersion:OmsRiskAdmissionResult.Collision);
            long version;await using(var q=Query(db,tx,command.Scope,$"SELECT version FROM oms_evidence_versions_v1 WHERE {Where}"))version=(long)(await q.ExecuteScalarAsync(ct))!;
            var stamp=OmsRecoveryRunCodec.Encode(controls.Stamp);
            await using(var q=Query(db,tx,command.Scope,"""
INSERT INTO oms_risk_cancel_admissions_v1(tenant_id,venue_id,environment,account_id,subaccount_id,command_id,parent_command_id,run_id,generation,schema_version,command_sha256,state_sha256,evidence_after,stamp_json,stamp_sha256)
VALUES(@tenant,@venue,@environment,@account,@subaccount,@command,@parent,@run,@generation,1,@commandSha,@stateSha,@version,@json,@sha)
"""))
            {
                q.Parameters.AddWithValue("command",command.CommandId.Value);q.Parameters.AddWithValue("parent",parent.Command.CommandId.Value);q.Parameters.AddWithValue("run",baseline.RunId);q.Parameters.AddWithValue("generation",publication.Token.Generation);
                q.Parameters.AddWithValue("commandSha",encoded.Sha256);q.Parameters.AddWithValue("stateSha",qualified.StateSha256);q.Parameters.AddWithValue("version",version);q.Parameters.AddWithValue("json",NpgsqlDbType.Jsonb,stamp.Json);q.Parameters.AddWithValue("sha",stamp.Sha256);await q.ExecuteNonQueryAsync(ct);
            }
            if(!qualified.IsCurrent)return await Reject(OmsRiskAdmissionResult.StaleBaseline);
            await AuditAsync(db,tx,command,publication,qualified,OmsRiskAdmissionResult.Reserved,ct);await tx.CommitAsync(ct);return OmsRiskAdmissionResult.Reserved;
        }
        await RetireProvenReservationsAsync(db, tx, command.Scope, baseline, publication, ct);
        var risk = await inputs.ReadAsync(command, baseline, ct);
        decimal pending, margin; long count;
        await using (var sum = Query(db, tx, command.Scope, $"""
SELECT COALESCE(sum(reserved_usd),0),COALESCE(sum(margin_usd),0),count(*) FROM oms_risk_reservations_v1
WHERE {Where} AND command_id NOT IN (SELECT command_id FROM oms_risk_settlements_v1 WHERE {Where})
"""))
        { await using var r = await sum.ExecuteReaderAsync(ct); await r.ReadAsync(ct); pending = r.GetDecimal(0); margin = r.GetDecimal(1); count = r.GetInt64(2); }
        if (risk is null || !OmsRiskLimitsV1.Allows(command, publication.StateSha256, risk, pending, margin, count,
            (clock ?? TimeProvider.System).GetUtcNow()))
        {
            var result=await Reject(OmsRiskAdmissionResult.Denied);
            if(risk is not null && (risk.Valuation.DailyLossUsd>=risk.Policy.MaxDailyLossUsd ||
                risk.Valuation.MarginUsedUsd>=Math.Min(risk.Policy.MaxMarginUsd,risk.Valuation.EquityUsd)))
                await new OmsPostgresAutomaticRiskCircuit(source).ActivateAsync(command.Scope,"Account loss or existing margin circuit activated.",ct);
            return result;
        }
        var write = await OmsPostgresJournal.PersistInTransactionAsync(db, tx, command, null, ct);
        if (write != OmsJournalWriteResult.Created) return await Reject(write == OmsJournalWriteResult.StaleVersion
            ? OmsRiskAdmissionResult.StaleOrderVersion : OmsRiskAdmissionResult.Collision);
        long after;
        await using (var counter = Query(db, tx, command.Scope, $"SELECT version FROM oms_evidence_versions_v1 WHERE {Where}"))
            after = (long)(await counter.ExecuteScalarAsync(ct))!;
        var audit = OmsRecoveryRunCodec.Encode(risk);
        await using (var reserve = Query(db, tx, command.Scope, """
INSERT INTO oms_risk_reservations_v1(tenant_id,venue_id,environment,account_id,subaccount_id,command_id,
 run_id,generation,schema_version,command_sha256,state_sha256,evidence_before,evidence_after,reserved_usd,margin_usd,inputs_json,inputs_sha256)
VALUES(@tenant,@venue,@environment,@account,@subaccount,@command,@run,@generation,1,@commandSha,@stateSha,@before,@after,@usd,@margin,@json,@auditSha)
"""))
        {
            reserve.Parameters.AddWithValue("command", command.CommandId.Value); reserve.Parameters.AddWithValue("run", publication.Token.Start.RunId);
            reserve.Parameters.AddWithValue("generation", publication.Token.Generation); reserve.Parameters.AddWithValue("commandSha", encoded.Sha256);
            reserve.Parameters.AddWithValue("stateSha", qualified.StateSha256); reserve.Parameters.AddWithValue("before", before); reserve.Parameters.AddWithValue("after", after);
            reserve.Parameters.AddWithValue("usd", risk.Valuation.OrderNotionalUsd); reserve.Parameters.AddWithValue("margin", risk.Valuation.IncrementalMarginUsd);
            reserve.Parameters.AddWithValue("json", NpgsqlDbType.Jsonb, audit.Json); reserve.Parameters.AddWithValue("auditSha", audit.Sha256);
            await reserve.ExecuteNonQueryAsync(ct);
        }
        if (!qualified.IsCurrent || !OmsRiskLimitsV1.Allows(command, publication.StateSha256, risk, pending, margin, count,
            (clock ?? TimeProvider.System).GetUtcNow())) return await Reject(OmsRiskAdmissionResult.StaleBaseline);
        await OmsPostgresRiskControls.BindAdmissionAsync(db, tx, command.CommandId, controls.Stamp, ct);
        await AuditAsync(db, tx, command, publication, qualified, OmsRiskAdmissionResult.Reserved, ct);
        await tx.CommitAsync(ct);
        // Ingress concurrent with COMMIT invalidates the live capability; this result never authorizes a send.
        return OmsRiskAdmissionResult.Reserved;
    }

    internal static async Task AuditAsync(NpgsqlConnection db, NpgsqlTransaction? tx, OmsJournalCommandRequest command,
        OmsRecoveryPublication publication, OmsQualifiedRecovery qualified, OmsRiskAdmissionResult result, CancellationToken ct)
    {
        await using var q = Query(db, tx, command.Scope, """
INSERT INTO oms_risk_admission_audit_v1(audit_id,schema_version,tenant_id,venue_id,environment,account_id,subaccount_id,
 command_id,command_sha256,result,baseline_run_id,state_sha256,actor_role)
VALUES(@audit,1,@tenant,@venue,@environment,@account,@subaccount,@command,@digest,@result,@run,@sha,current_user)
""");
        q.Parameters.AddWithValue("audit", Guid.NewGuid()); q.Parameters.AddWithValue("command", command.CommandId.Value);
        q.Parameters.AddWithValue("digest", OmsJournalCanonical.EncodeCommand(command).Sha256);
        q.Parameters.AddWithValue("result", (short)result); q.Parameters.AddWithValue("run", publication.Token.Start.RunId);
        q.Parameters.AddWithValue("sha", qualified.StateSha256); await q.ExecuteNonQueryAsync(ct);
    }

    internal static async Task AuditUnavailableAsync(NpgsqlDataSource source,OmsJournalCommandRequest command,CancellationToken ct)
    {
        // Explicitly absent baseline, always a denial. These sentinel values never create a capability.
        await using var db=await source.OpenConnectionAsync(ct);await using var q=Query(db,null,command.Scope,"""
INSERT INTO oms_risk_admission_audit_v1(audit_id,schema_version,tenant_id,venue_id,environment,account_id,subaccount_id,command_id,command_sha256,result,baseline_run_id,state_sha256,actor_role)
VALUES(@audit,1,@tenant,@venue,@environment,@account,@subaccount,@command,@sha,5,'00000000-0000-0000-0000-000000000000',decode(repeat('00',32),'hex'),current_user)
""");
        q.Parameters.AddWithValue("audit",Guid.NewGuid());q.Parameters.AddWithValue("command",command.CommandId.Value);q.Parameters.AddWithValue("sha",OmsJournalCanonical.EncodeCommand(command).Sha256);await q.ExecuteNonQueryAsync(ct);
    }

    internal static async Task<DeribitNativeCommandView?> ReadCancelParentAsync(NpgsqlConnection db,NpgsqlTransaction tx,OmsJournalCommandRequest cancel,CancellationToken ct)
    {
        var ws=await ReadWsCancelParentAsync(db,tx,cancel,ct);
        var native=(await OmsPostgresFixCommandEvidenceV1.ViewsAsync(db,tx,cancel.Scope,ct)).Where(x=>
            x.Command.Payload.Kind==OmsJournalCommandKind.Place&&x.Command.LocalOrderId==cancel.LocalOrderId&&
            x.Order.ExchangeOrderId==cancel.Payload.TargetExchangeOrderId).ToArray();
        if(native.Length+(ws is null?0:1)!=1)return null;
        if(ws is not null)return DeribitNativeCommandView.FromWs(ws);
        var view=native[0];
        return await HasReservation(db,tx,view,ct)?view:null;
    }
    private static async Task<bool> HasReservation(NpgsqlConnection db,NpgsqlTransaction tx,DeribitNativeCommandView view,CancellationToken ct)
    {
        await using var q=Query(db,tx,view.Command.Scope,"SELECT command_sha256 FROM oms_risk_reservations_v1 WHERE "+Where+" AND command_id=@command");
        q.Parameters.AddWithValue("command",view.Command.CommandId.Value);
        return await q.ExecuteScalarAsync(ct) is byte[] sha&&sha.SequenceEqual(OmsJournalCanonical.EncodeCommand(view.Command).Sha256);
    }

    private static async Task<DeribitJsonRpcCommandReceipt?> ReadWsCancelParentAsync(NpgsqlConnection db,NpgsqlTransaction tx,OmsJournalCommandRequest cancel,CancellationToken ct)
    {
        if(cancel.Payload.TargetExchangeOrderId is null)return null;
        await using var q=Query(db,tx,cancel.Scope,"""
SELECT c.request_json::text,c.payload_sha256,p.receipt_json::text,p.receipt_sha256,r.command_sha256
FROM oms_risk_reservations_v1 r JOIN oms_commands c USING(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
JOIN oms_deribit_command_receipts_v1 p USING(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
WHERE r.tenant_id=@tenant AND r.venue_id=@venue AND r.environment=@environment AND r.account_id=@account AND r.subaccount_id=@subaccount
AND c.aggregate_id=@aggregate AND p.native_order_id=@native AND p.command_kind='Place'
AND octet_length(c.request_json::text)<=16384 AND octet_length(p.receipt_json::text)<=32768 LIMIT 2
""");
        q.Parameters.AddWithValue("aggregate",cancel.LocalOrderId.Value);q.Parameters.AddWithValue("native",cancel.Payload.TargetExchangeOrderId);
        await using var row=await q.ExecuteReaderAsync(ct);if(!await row.ReadAsync(ct))return null;
        var original=OmsJournalRecoveryCommandCodec.Decode(row.GetString(0),cancel.Scope);var receipt=DeribitCommandReceiptCanonical.Decode(row.GetString(2),original);
        var hash=OmsJournalCanonical.EncodeCommand(original).Sha256;
        if(!hash.SequenceEqual(row.GetFieldValue<byte[]>(1))||!hash.SequenceEqual(row.GetFieldValue<byte[]>(4))||!DeribitCommandReceiptCanonical.Encode(receipt).Sha256.SequenceEqual(row.GetFieldValue<byte[]>(3))||await row.ReadAsync(ct))return null;
        return receipt;
    }

    private static async Task RetireProvenReservationsAsync(NpgsqlConnection db, NpgsqlTransaction tx, AccountKey scope,
        OmsRecoveredAccountStateV1 baseline, OmsRecoveryPublication publication, CancellationToken ct)
    {
        var candidates = new List<DeribitNativeCommandView>();
        await using (var q = Query(db, tx, scope, """
SELECT c.request_json::text,c.payload_sha256,r.command_sha256,p.receipt_json::text,p.receipt_sha256,p.command_sha256
FROM oms_risk_reservations_v1 r JOIN oms_commands c USING(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
JOIN oms_deribit_command_receipts_v1 p USING(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
LEFT JOIN oms_risk_settlements_v1 s USING(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
WHERE r.tenant_id=@tenant AND r.venue_id=@venue AND r.environment=@environment AND r.account_id=@account AND r.subaccount_id=@subaccount
AND s.command_id IS NULL AND octet_length(c.request_json::text)<=16384 AND octet_length(p.receipt_json::text)<=32768 LIMIT 10001
"""))
        {
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (candidates.Count >= 10000) throw new InvalidOperationException("Settlement capacity exceeded.");
                var original = OmsJournalRecoveryCommandCodec.Decode(r.GetString(0), scope);
                var hash = OmsJournalCanonical.EncodeCommand(original).Sha256;
                var receipt = DeribitCommandReceiptCanonical.Decode(r.GetString(3), original);
                if (!hash.SequenceEqual(r.GetFieldValue<byte[]>(1)) || !hash.SequenceEqual(r.GetFieldValue<byte[]>(2)) ||
                    !hash.SequenceEqual(r.GetFieldValue<byte[]>(5)) || !DeribitCommandReceiptCanonical.Encode(receipt).Sha256.SequenceEqual(r.GetFieldValue<byte[]>(4)))
                    throw new InvalidOperationException("Settlement evidence integrity failure.");
                candidates.Add(DeribitNativeCommandView.FromWs(receipt));
            }
        }
        foreach(var native in (await OmsPostgresFixCommandEvidenceV1.ViewsAsync(db,tx,scope,ct)).Where(x=>x.Command.Payload.Kind==OmsJournalCommandKind.Place))
            if(await HasReservation(db,tx,native,ct))candidates.Add(native);
        foreach (var receipt in candidates.Where(x => OmsRiskSettlementV1.CanRetire(baseline, x)))
        {
            await using var q = Query(db, tx, scope, """
INSERT INTO oms_risk_settlements_v1(tenant_id,venue_id,environment,account_id,subaccount_id,command_id,run_id,generation,schema_version,state_sha256)
VALUES(@tenant,@venue,@environment,@account,@subaccount,@command,@run,@generation,1,@sha) ON CONFLICT DO NOTHING
""");
            q.Parameters.AddWithValue("command", receipt.Command.CommandId.Value); q.Parameters.AddWithValue("run", baseline.RunId);
            q.Parameters.AddWithValue("generation", publication.Token.Generation); q.Parameters.AddWithValue("sha", Convert.FromHexString(publication.StateSha256));
            await q.ExecuteNonQueryAsync(ct);
        }
    }
}
