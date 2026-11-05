using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>
/// Read-only capture ownership, not physical-send fencing. A lost DB session stops capture;
/// observations around a lost-lock/commit race remain subject to normal inbox dedup.
/// </summary>
public sealed class OmsPostgresPrivateCaptureOwner : IOmsPrivateEventCapture, IAsyncDisposable
{
    private readonly NpgsqlConnection lease;
    private readonly OmsPostgresPrivateEventCapture capture;
    private readonly AccountKey scope;
    private readonly long key;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    private OmsPostgresPrivateCaptureOwner(NpgsqlConnection lease, NpgsqlDataSource source, AccountKey scope, long key)
    { this.lease = lease; capture = new(source); this.scope = scope; this.key = key; }

    public static async Task<OmsPostgresPrivateCaptureOwner> AcquireAsync(NpgsqlDataSource source, AccountKey scope,
        CancellationToken ct = default)
    {
        NpgsqlConnection? db = null;
        try
        {
            db = await source.OpenConnectionAsync(ct);
            await using var role = new NpgsqlCommand("""
SELECT NOT (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls)
 AND current_user=session_user AND NOT has_schema_privilege(current_user,'public','CREATE')
FROM pg_roles WHERE rolname=current_user
""", db);
            if (await role.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException();
            await using var grants = new NpgsqlCommand("""
SELECT bool_and(has_table_privilege(current_user, relation, privilege)) FROM (VALUES
 ('public.oms_inbox','SELECT'),('public.oms_inbox','INSERT'),('public.oms_inbox','UPDATE'),
 ('public.oms_private_capture_observations','SELECT'),('public.oms_private_capture_observations','INSERT'),
 ('public.oms_private_capture_observations','UPDATE'),
 ('public.oms_integrity_incidents','SELECT'),('public.oms_integrity_incidents','INSERT')
) required(relation,privilege)
""", db);
            if (await grants.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException();
            await using var forbidden = new NpgsqlCommand("""
SELECT NOT EXISTS (
 SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
 WHERE n.nspname='public' AND (
  (c.relname IN ('oms_inbox','oms_private_capture_observations','oms_integrity_incidents')
   AND has_table_privilege(current_user,c.oid,'DELETE,TRUNCATE')) OR
  (c.relname='oms_integrity_incidents' AND has_table_privilege(current_user,c.oid,'UPDATE')) OR
  (c.relname IN ('Orders','Fills','oms_intents','oms_commands','oms_outbox','oms_attempts','oms_aggregates','oms_account_leases','oms_deribit_command_receipts_v1')
   AND has_table_privilege(current_user,c.oid,'INSERT,UPDATE,DELETE,TRUNCATE'))))
 AND has_sequence_privilege(current_user,'public.oms_integrity_incidents_incident_id_seq','USAGE')
""", db);
            if (await forbidden.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException();

            // Separate namespace from all event/capture arbitration keys. Full scope, never a symbol.
            var identity = JsonSerializer.SerializeToUtf8Bytes(new[] { "OMS-B3-readonly-host:v1", scope.Tenant.Value,
                scope.Venue.Value, ((int)scope.Environment).ToString(System.Globalization.CultureInfo.InvariantCulture),
                scope.Account.Value, scope.Subaccount?.Value ?? "" });
            var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(identity));
            await using var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", db);
            acquire.Parameters.AddWithValue("key", key);
            if (await acquire.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException();
            return new(db, source, scope, key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (db is not null) await db.DisposeAsync();
            throw new InvalidOperationException("Read-only capture database/ownership gate refused.");
        }
        catch
        {
            if (db is not null) await db.DisposeAsync();
            throw;
        }
    }

    public async Task<OmsPrivateCaptureResult> CaptureAsync(OmsPrivateEventEnvelope envelope, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (envelope.Scope != scope) throw new InvalidOperationException("Capture ownership scope mismatch.");
            // The connection never reconnects/reacquires silently. A lost lease fails before further capture.
            await using var alive = new NpgsqlCommand("SELECT 1", lease);
            await alive.ExecuteScalarAsync(ct);
            return await capture.CaptureAsync(envelope, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        { throw new InvalidOperationException("Read-only capture database/ownership gate failed."); }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            try
            {
                await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", lease) { CommandTimeout = 3 };
                release.Parameters.AddWithValue("key", key);
                await release.ExecuteScalarAsync();
            }
            catch { /* Broken sessions already lost the lock; disposal still closes this connection only. */ }
            finally { await lease.DisposeAsync(); }
        }
        finally { gate.Release(); }
    }
}
