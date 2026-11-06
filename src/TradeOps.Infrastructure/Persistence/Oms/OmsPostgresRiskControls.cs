using Npgsql;
using NpgsqlTypes;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>Durable latch and read-only revision checks. Neither locks nor a matching stamp fence
/// a physical transport. No cancellation/flatten RPCs, migrations, grants or default provisioning.</summary>
public sealed class OmsPostgresRiskControls(NpgsqlDataSource source)
{
    private const string Where = "level=@level AND tenant_id=@tenant AND venue_id=@venue AND environment=@environment AND account_id=@account AND subaccount_id=@subaccount";
    internal static NpgsqlCommand Query(NpgsqlConnection db, NpgsqlTransaction? tx, OmsRiskControlScopeV1 scope, string sql)
    {
        scope.Validate(); var q = new NpgsqlCommand(sql, db, tx);
        q.Parameters.AddWithValue("level", (short)scope.Level); q.Parameters.AddWithValue("tenant", scope.Tenant);
        q.Parameters.AddWithValue("venue", scope.Venue); q.Parameters.AddWithValue("environment", scope.Environment);
        q.Parameters.AddWithValue("account", scope.Account); q.Parameters.AddWithValue("subaccount", scope.Subaccount); return q;
    }

    public async Task<OmsRiskControlEventV1> SetAsync(OmsRiskControlRequestV1 request, CancellationToken ct = default)
    {
        request.Validate(); await using var db = await source.OpenConnectionAsync(ct);
        await using var q = Query(db, null, request.Scope, """
SELECT revision,actor_role::text,recorded_at FROM public.oms_set_risk_control_v1(
 @level,@tenant,@venue,@environment,@account,@subaccount,@kind,@decision,@expected,@blocked,@reason)
""");
        q.Parameters.AddWithValue("kind", (short)request.Kind); q.Parameters.AddWithValue("decision", request.DecisionId);
        q.Parameters.AddWithValue("expected", request.ExpectedRevision); q.Parameters.AddWithValue("blocked", request.Blocked);
        q.Parameters.AddWithValue("reason", request.Reason);
        // Fully drain the command before returning: a row before COMMIT acknowledgement is not success.
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Risk control event missing.");
        var result = new OmsRiskControlEventV1(1, request, r.GetInt64(0), r.GetString(1), new(r.GetDateTime(2)));
        while (await r.ReadAsync(ct)) throw new InvalidOperationException("Multiple risk control events returned.");
        while (await r.NextResultAsync(ct)) { }
        return result;
    }

    internal static async Task<(bool Blocked, OmsRiskControlStampV1 Stamp)> LockAndReadAsync(
        NpgsqlConnection db, NpgsqlTransaction tx, AccountKey account, CancellationToken ct)
    {
        var scopes = new[] { OmsRiskControlScopeV1.Global, OmsRiskControlScopeV1.ForVenue(account), OmsRiskControlScopeV1.ForAccount(account) };
        // All admissions take locks in the same global -> venue -> account order before journal locks.
        // A control writer takes exactly one exclusive lock. Shared locks are retained through COMMIT.
        foreach (var scope in scopes)
        {
            await using var q = Query(db, tx, scope, "SELECT pg_advisory_xact_lock_shared(public.oms_risk_control_lock_key_v1(@level,@tenant,@venue,@environment,@account,@subaccount))");
            await q.ExecuteNonQueryAsync(ct);
        }
        var revisions = new long[6]; var blocked = false;
        for (var i = 0; i < scopes.Length; i++)
        {
            await using var q = Query(db, tx, scopes[i], $"""
SELECT DISTINCT ON(kind) kind,revision,blocked FROM oms_risk_control_events_v1
WHERE {Where} ORDER BY kind,revision DESC
""");
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var kind = r.GetInt16(0);
                if (kind is < 1 or > 2 || r.GetInt64(1) <= 0) throw new InvalidOperationException("Invalid risk control state.");
                revisions[2 * i + kind - 1] = r.GetInt64(1); blocked |= r.GetBoolean(2);
            }
        }
        return (blocked, new(1, account, revisions[0], revisions[1], revisions[2], revisions[3], revisions[4], revisions[5]));
    }

    internal static async Task BindAdmissionAsync(NpgsqlConnection db, NpgsqlTransaction tx, OmsCommandId command,
        OmsRiskControlStampV1 stamp, CancellationToken ct)
    {
        var encoded = OmsRecoveryRunCodec.Encode(stamp);
        await using var q = Query(db, tx, OmsRiskControlScopeV1.ForAccount(stamp.Scope), """
INSERT INTO oms_risk_admission_controls_v1(tenant_id,venue_id,environment,account_id,subaccount_id,command_id,
 schema_version,stamp_json,stamp_sha256) VALUES(@tenant,@venue,@environment,@account,@subaccount,@command,1,@json,@sha)
""");
        q.Parameters.AddWithValue("command", command.Value); q.Parameters.AddWithValue("json", NpgsqlDbType.Jsonb, encoded.Json);
        q.Parameters.AddWithValue("sha", encoded.Sha256); await q.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> IsReservationControlCurrentAsync(AccountKey account, OmsCommandId command, CancellationToken ct = default)
    {
        await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct);
        var current = await LockAndReadAsync(db, tx, account, ct);
        if (current.Blocked) return false;
        await using var q = Query(db, tx, OmsRiskControlScopeV1.ForAccount(account), """
SELECT schema_version,stamp_json::text,stamp_sha256 FROM oms_risk_admission_controls_v1
 WHERE tenant_id=@tenant AND venue_id=@venue AND environment=@environment AND account_id=@account AND subaccount_id=@subaccount AND command_id=@command
""");
        q.Parameters.AddWithValue("command", command.Value); await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct) || r.GetInt32(0) != 1) return false;
        var prior = OmsRecoveryRunCodec.Decode<OmsRiskControlStampV1>(r.GetString(1), r.GetFieldValue<byte[]>(2));
        return prior == current.Stamp;
    }
}
