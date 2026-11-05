using System.Data;
using Npgsql;
using NpgsqlTypes;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>Recovery metadata only; never changes dispatch phases, receipts, positions or sender ownership.
/// Every writer locks the same scoped head. Superseded epochs can retain history but cannot publish a result.</summary>
public sealed class OmsPostgresRecoveryRunStore(NpgsqlDataSource source) : IOmsRecoveryRunStore
{
    private const string Where = "tenant_id=@tenant AND venue_id=@venue AND environment=@environment AND account_id=@account AND subaccount_id=@subaccount";
    private const string Columns = "tenant_id,venue_id,environment,account_id,subaccount_id";
    private const string Values = "@tenant,@venue,@environment,@account,@subaccount";

    public async Task<OmsRecoveryRunToken> BeginAsync(OmsRecoveryRunStart start, CancellationToken ct = default)
    {
        start.Validate(); var encoded = OmsRecoveryRunCodec.Encode(start);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // Only metadata arbitration. No physical sender authority is implied by this lock.
        await using (var arbitrate = Command("SELECT pg_advisory_xact_lock(hashtextextended(@key,0))", db, tx, start.Scope))
        {
            arbitrate.Parameters.AddWithValue("key", "recovery-run-v1:" + OmsRecoveryRunCodec.Encode(start.Scope).Json);
            await arbitrate.ExecuteNonQueryAsync(ct);
        }
        await using (var prior = Command($"SELECT generation,start_json::text,start_sha256 FROM oms_recovery_runs_v1 WHERE {Where} AND run_id=@run", db, tx, start.Scope))
        {
            prior.Parameters.AddWithValue("run", start.RunId);
            await using var rows = await prior.ExecuteReaderAsync(ct);
            if (await rows.ReadAsync(ct))
            {
                var known = OmsRecoveryRunCodec.Decode<OmsRecoveryRunStart>(rows.GetString(1), rows.GetFieldValue<byte[]>(2));
                if (known != start) throw new InvalidOperationException("Recovery run identity collision.");
                // Lost commit ACK can retry Begin without creating a new generation or resetting state.
                return new(start, rows.GetInt64(0));
            }
        }
        long generation;
        await using (var next = Command($"SELECT COALESCE(max(generation),0)+1 FROM oms_recovery_runs_v1 WHERE {Where}", db, tx, start.Scope))
            generation = (long)(await next.ExecuteScalarAsync(ct))!;
        await using (var insert = Command($"""
INSERT INTO oms_recovery_runs_v1 ({Columns},run_id,epoch,generation,schema_version,start_json,start_sha256)
VALUES ({Values},@run,@epoch,@generation,1,@json,@sha)
""", db, tx, start.Scope))
        {
            insert.Parameters.AddWithValue("run", start.RunId); insert.Parameters.AddWithValue("epoch", start.Epoch);
            insert.Parameters.AddWithValue("generation", generation); AddJson(insert, encoded);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using (var head = Command($"""
INSERT INTO oms_recovery_heads_v1 ({Columns},run_id,state) VALUES ({Values},@run,'Recovering')
ON CONFLICT ({Columns}) DO UPDATE SET run_id=EXCLUDED.run_id,state='Recovering',updated_at=clock_timestamp()
""", db, tx, start.Scope))
        { head.Parameters.AddWithValue("run", start.RunId); await head.ExecuteNonQueryAsync(ct); }
        var token = new OmsRecoveryRunToken(start, generation);
        await Event(db, tx, token, OmsRecoveryRunState.Recovering, "Begin", ct);
        await tx.CommitAsync(ct); return token;
    }

    public async Task MarkReconcilingAsync(OmsRecoveryRunToken token, CancellationToken ct = default)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var state = await LockHead(db, tx, token, ct);
        if (state == OmsRecoveryRunState.Reconciling) return;
        if (state != OmsRecoveryRunState.Recovering) throw new InvalidOperationException("Recovery cannot reconcile from its current state.");
        await Update(db, tx, token, OmsRecoveryRunState.Reconciling, "Reconcile", ct);
        await tx.CommitAsync(ct);
    }

    public async Task CompleteBlockedAsync(OmsRecoveryRunToken token, DeribitRecoveryRunDocumentV1 document, CancellationToken ct = default)
    {
        document.Validate();
        if (document.Start != token.Start) throw new ArgumentException("Recovery result binding mismatch.");
        var encoded = OmsRecoveryRunCodec.Encode(document);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var state = await LockHead(db, tx, token, ct);
        await using (var known = Command($"SELECT document_sha256 FROM oms_deribit_recovery_results_v1 WHERE {Where} AND run_id=@run", db, tx, token.Start.Scope))
        {
            known.Parameters.AddWithValue("run", token.Start.RunId);
            if (await known.ExecuteScalarAsync(ct) is byte[] digest)
            {
                if (!digest.SequenceEqual(encoded.Sha256)) throw new InvalidOperationException("Recovery result collision; original evidence preserved.");
                return; // idempotent replay after a lost result-commit ACK; never changes the head
            }
        }
        if (state != OmsRecoveryRunState.Reconciling) throw new InvalidOperationException("Recovery result cannot be committed in this state.");
        await using (var insert = Command($"""
INSERT INTO oms_deribit_recovery_results_v1 ({Columns},run_id,schema_version,document_json,document_sha256)
VALUES ({Values},@run,1,@json,@sha)
""", db, tx, token.Start.Scope))
        {
            insert.Parameters.AddWithValue("run", token.Start.RunId); AddJson(insert, encoded);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await Update(db, tx, token, OmsRecoveryRunState.Blocked, "CompletedWithIssues", ct);
        await tx.CommitAsync(ct);
    }

    public async Task StopAsync(OmsRecoveryRunToken token, OmsRecoveryStopReason reason, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(reason) || reason == OmsRecoveryStopReason.CompletedWithIssues)
            throw new ArgumentException("Completion requires its actual recovery document.");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var state = await LockHead(db, tx, token, ct);
        if (state == OmsRecoveryRunState.Disconnected) return;
        await Update(db, tx, token, OmsRecoveryRunState.Disconnected, reason.ToString(), ct);
        await tx.CommitAsync(ct);
    }

    public async Task<OmsRecoveryRunHead?> ReadHeadAsync(AccountKey scope, CancellationToken ct = default)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var query = Command($"""
SELECT r.start_json::text,r.start_sha256,r.generation,h.state,h.updated_at
FROM oms_recovery_heads_v1 h JOIN oms_recovery_runs_v1 r USING ({Columns},run_id)
WHERE h.tenant_id=@tenant AND h.venue_id=@venue AND h.environment=@environment AND h.account_id=@account AND h.subaccount_id=@subaccount
""", db, null, scope);
        await using var rows = await query.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) return null;
        var start = OmsRecoveryRunCodec.Decode<OmsRecoveryRunStart>(rows.GetString(0), rows.GetFieldValue<byte[]>(1)); start.Validate();
        if (start.Scope != scope || !Enum.TryParse<OmsRecoveryRunState>(rows.GetString(3), out var state) || !Enum.IsDefined(state))
            throw new InvalidOperationException("Recovery head integrity failure.");
        return new(new(start, rows.GetInt64(2)), state, new(rows.GetDateTime(4)));
    }

    public async Task<DeribitRecoveryRunDocumentV1?> ReadResultAsync(AccountKey scope, Guid runId, CancellationToken ct = default)
    {
        if (runId == Guid.Empty) throw new ArgumentException("Recovery run required.");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var query = Command($"""
SELECT schema_version,CASE WHEN octet_length(document_json::text)<=@capacity THEN document_json::text END,document_sha256
FROM oms_deribit_recovery_results_v1 WHERE {Where} AND run_id=@run
""", db, null, scope);
        query.Parameters.AddWithValue("run", runId); query.Parameters.AddWithValue("capacity", OmsRecoveryRunCodec.MaximumDocumentBytes);
        await using var rows = await query.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) return null;
        if (rows.GetInt32(0) != 1 || rows.IsDBNull(1)) throw new InvalidOperationException("Unsupported or excessive recovery document.");
        var document = OmsRecoveryRunCodec.Decode<DeribitRecoveryRunDocumentV1>(rows.GetString(1), rows.GetFieldValue<byte[]>(2));
        document.Validate();
        if (document.Start.Scope != scope || document.Start.RunId != runId) throw new InvalidOperationException("Recovery result integrity failure.");
        return document;
    }

    private static async Task<OmsRecoveryRunState> LockHead(NpgsqlConnection db, NpgsqlTransaction tx, OmsRecoveryRunToken token, CancellationToken ct)
    {
        token.Start.Validate();
        await using var query = Command($"""
SELECT h.run_id,r.epoch,r.generation,h.state,r.start_json::text,r.start_sha256
FROM oms_recovery_heads_v1 h JOIN oms_recovery_runs_v1 r USING ({Columns},run_id)
WHERE h.tenant_id=@tenant AND h.venue_id=@venue AND h.environment=@environment AND h.account_id=@account AND h.subaccount_id=@subaccount
FOR UPDATE OF h
""", db, tx, token.Start.Scope);
        await using var rows = await query.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct) || rows.GetGuid(0) != token.Start.RunId || rows.GetGuid(1) != token.Start.Epoch ||
            rows.GetInt64(2) != token.Generation || OmsRecoveryRunCodec.Decode<OmsRecoveryRunStart>(rows.GetString(4), rows.GetFieldValue<byte[]>(5)) != token.Start)
            throw new InvalidOperationException("Recovery run was superseded or its binding is invalid.");
        return Enum.Parse<OmsRecoveryRunState>(rows.GetString(3));
    }

    private static async Task Update(NpgsqlConnection db, NpgsqlTransaction tx, OmsRecoveryRunToken token, OmsRecoveryRunState state, string reason, CancellationToken ct)
    {
        await using var update = Command($"UPDATE oms_recovery_heads_v1 SET state=@state,updated_at=clock_timestamp() WHERE {Where} AND run_id=@run", db, tx, token.Start.Scope);
        update.Parameters.AddWithValue("state", state.ToString()); update.Parameters.AddWithValue("run", token.Start.RunId);
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Recovery head disappeared.");
        await Event(db, tx, token, state, reason, ct);
    }

    private static async Task Event(NpgsqlConnection db, NpgsqlTransaction tx, OmsRecoveryRunToken token, OmsRecoveryRunState state, string reason, CancellationToken ct)
    {
        await using var insert = Command($"INSERT INTO oms_recovery_events_v1 ({Columns},run_id,state,reason) VALUES ({Values},@run,@state,@reason)", db, tx, token.Start.Scope);
        insert.Parameters.AddWithValue("run", token.Start.RunId); insert.Parameters.AddWithValue("state", state.ToString()); insert.Parameters.AddWithValue("reason", reason);
        await insert.ExecuteNonQueryAsync(ct);
    }

    private static NpgsqlCommand Command(string sql, NpgsqlConnection db, NpgsqlTransaction? tx, AccountKey scope)
    {
        var command = new NpgsqlCommand(sql, db, tx);
        command.Parameters.AddWithValue("tenant", scope.Tenant.Value); command.Parameters.AddWithValue("venue", scope.Venue.Value);
        command.Parameters.AddWithValue("environment", (short)scope.Environment); command.Parameters.AddWithValue("account", scope.Account.Value);
        command.Parameters.AddWithValue("subaccount", scope.Subaccount?.Value ?? ""); return command;
    }
    private static void AddJson(NpgsqlCommand command, (string Json, byte[] Sha256) encoded)
    {
        command.Parameters.AddWithValue("json", NpgsqlDbType.Jsonb, encoded.Json);
        command.Parameters.AddWithValue("sha", encoded.Sha256);
    }
}
