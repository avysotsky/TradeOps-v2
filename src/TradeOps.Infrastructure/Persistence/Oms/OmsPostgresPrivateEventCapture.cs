using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>Capture-only implementation. Not registered in live DI and never writes legacy Orders/Fills.</summary>
public sealed class OmsPostgresPrivateEventCapture(NpgsqlDataSource source) : IOmsPrivateEventCapture
{
    private const string ScopeWhere = """
tenant_id=@tenant AND venue_id=@venue AND environment=@environment
 AND account_id=@account AND subaccount_id=@subaccount
""";

    public async Task<OmsPrivateCaptureResult> CaptureAsync(OmsPrivateEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var encoded = OmsPrivateEventCanonical.Encode(envelope);
        var observation = OmsPrivateEventCanonical.EncodeObservation(envelope, encoded);
        await using var db = await source.OpenConnectionAsync(cancellationToken);
        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        // Full-scope CaptureId arbitration protects retry identity. No physical-send authority.
        var lockJson = JsonSerializer.Serialize(new[] { "OMS-B3-capture-v1", envelope.Scope.Tenant.Value,
            envelope.Scope.Venue.Value, ((int)envelope.Scope.Environment).ToString(System.Globalization.CultureInfo.InvariantCulture),
            envelope.Scope.Account.Value, envelope.Scope.Subaccount?.Value ?? "", envelope.Observation.CaptureId.ToString("N") });
        var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(lockJson)));
        await using (var lease = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", db, tx))
        {
            lease.Parameters.AddWithValue("key", key);
            await lease.ExecuteNonQueryAsync(cancellationToken);
        }

        var hasObservation = false;
        OmsPrivateCaptureResult? previous = null;
        await using (var query = Command($"""
SELECT observation_json=@json, disposition FROM oms_private_capture_observations
WHERE {ScopeWhere} AND capture_id=@capture FOR UPDATE
""", db, tx, envelope.Scope))
        {
            query.Parameters.AddWithValue("capture", envelope.Observation.CaptureId);
            query.Parameters.Add(new NpgsqlParameter("json", NpgsqlDbType.Jsonb) { Value = observation.Json });
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                hasObservation = true;
                if (reader.GetBoolean(0)) previous = (OmsPrivateCaptureResult)reader.GetInt16(1);
            }
        }
        if (previous.HasValue)
        {
            await tx.CommitAsync(cancellationToken);
            // Retrying a committed observation must never report a new event insertion.
            return previous == OmsPrivateCaptureResult.Collision
                ? OmsPrivateCaptureResult.Collision : OmsPrivateCaptureResult.Duplicate;
        }

        var result = OmsPrivateCaptureResult.Collision;
        if (!hasObservation)
        {
            await using var insert = Command("""
INSERT INTO oms_inbox
(tenant_id,venue_id,environment,account_id,subaccount_id,event_namespace,event_id,
 event_kind,aggregate_id,schema_version,payload_sha256,event_json)
VALUES (@tenant,@venue,@environment,@account,@subaccount,@namespace,@event,
 @kind,NULL,2,@digest,@json) ON CONFLICT DO NOTHING
""", db, tx, envelope.Scope);
            EventParameters(insert, encoded);
            insert.Parameters.AddWithValue("kind", envelope.Facts.Kind.ToString());
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 1)
                result = OmsPrivateCaptureResult.Inserted;
            else
            {
                await using var compare = Command($"""
SELECT payload_sha256=@digest OR event_json=@json FROM oms_inbox
WHERE {ScopeWhere} AND event_namespace=@namespace AND event_id=@event FOR UPDATE
""", db, tx, envelope.Scope);
                EventParameters(compare, encoded);
                var matches = (bool?)await compare.ExecuteScalarAsync(cancellationToken)
                    ?? throw new InvalidOperationException("Conflicting private inbox event disappeared.");
                result = matches ? OmsPrivateCaptureResult.Duplicate : OmsPrivateCaptureResult.Collision;
            }
        }

        if (result == OmsPrivateCaptureResult.Collision)
        {
            await using var incident = Command("""
INSERT INTO oms_integrity_incidents
(tenant_id,venue_id,environment,account_id,subaccount_id,kind,correlation)
VALUES (@tenant,@venue,@environment,@account,@subaccount,'EventCollision',@correlation)
""", db, tx, envelope.Scope);
            incident.Parameters.AddWithValue("correlation", hasObservation
                ? "B3:capture:" + envelope.Observation.CaptureId.ToString("N")
                : encoded.Namespace + ":" + encoded.EventId);
            await incident.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var save = Command("""
INSERT INTO oms_private_capture_observations
(tenant_id,venue_id,environment,account_id,subaccount_id,capture_id,observation_sha256,
 schema_version,event_namespace,event_id,stream_epoch,disposition,observation_json)
VALUES (@tenant,@venue,@environment,@account,@subaccount,@capture,@digest,
 1,@namespace,@event,@epoch,@disposition,@json)
""", db, tx, envelope.Scope))
        {
            save.Parameters.AddWithValue("capture", envelope.Observation.CaptureId);
            save.Parameters.AddWithValue("digest", observation.Sha256);
            save.Parameters.AddWithValue("namespace", encoded.Namespace);
            save.Parameters.AddWithValue("event", encoded.EventId);
            save.Parameters.AddWithValue("epoch", envelope.Observation.StreamEpoch);
            save.Parameters.AddWithValue("disposition", (short)result);
            save.Parameters.Add(new NpgsqlParameter("json", NpgsqlDbType.Jsonb) { Value = observation.Json });
            await save.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
        return result;
    }

    private static NpgsqlCommand Command(string sql, NpgsqlConnection db, NpgsqlTransaction tx, AccountKey scope)
    {
        var command = new NpgsqlCommand(sql, db, tx);
        command.Parameters.AddWithValue("tenant", scope.Tenant.Value);
        command.Parameters.AddWithValue("venue", scope.Venue.Value);
        command.Parameters.AddWithValue("environment", (short)scope.Environment);
        command.Parameters.AddWithValue("account", scope.Account.Value);
        command.Parameters.AddWithValue("subaccount", scope.Subaccount?.Value ?? "");
        return command;
    }

    private static void EventParameters(NpgsqlCommand command, OmsPrivateEncodedEvent encoded)
    {
        command.Parameters.AddWithValue("namespace", encoded.Namespace);
        command.Parameters.AddWithValue("event", encoded.EventId);
        command.Parameters.AddWithValue("digest", encoded.Sha256);
        command.Parameters.Add(new NpgsqlParameter("json", NpgsqlDbType.Jsonb) { Value = encoded.Json });
    }
}
