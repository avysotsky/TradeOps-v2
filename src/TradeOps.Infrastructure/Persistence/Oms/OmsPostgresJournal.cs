using System.Data;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>
/// Isolated PostgreSQL store. No exchange interfaces, timers, hosted services, dispatcher or DI registration.
/// Commands can be journalled but never physically sent by this class.
/// </summary>
public sealed class OmsPostgresJournal(NpgsqlDataSource dataSource)
{
    private const string ScopeWhere = """
tenant_id=@tenant AND venue_id=@venue AND environment=@environment
 AND account_id=@account AND subaccount_id=@subaccount
""";

    private static NpgsqlCommand Command(string sql, NpgsqlConnection connection,
        NpgsqlTransaction? transaction, AccountKey scope)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant", scope.Tenant.Value);
        command.Parameters.AddWithValue("venue", scope.Venue.Value);
        command.Parameters.AddWithValue("environment", (short)scope.Environment);
        command.Parameters.AddWithValue("account", scope.Account.Value);
        command.Parameters.AddWithValue("subaccount", scope.Subaccount?.Value ?? "");
        return command;
    }

    private static void AddJson(NpgsqlCommand cmd, string json, byte[] digest)
    {
        cmd.Parameters.Add(new NpgsqlParameter("json", NpgsqlDbType.Jsonb) { Value = json });
        cmd.Parameters.Add(new NpgsqlParameter("digest", NpgsqlDbType.Bytea) { Value = digest });
    }

    /// <summary>
    /// All of aggregate CAS, intent, immutable command and outbox commit together.
    /// Same ID + same canonical request returns Duplicate; same ID + different request returns Collision.
    /// No outbound send occurs. Injected failpoint is for rollback/crash tests only.
    /// </summary>
    public async Task<OmsJournalWriteResult> PersistAsync(
        OmsJournalCommandRequest request,
        Func<OmsJournalFailPoint, Task>? fault = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (json, digest) = OmsJournalCanonical.EncodeCommand(request);

        await using var db = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = (NpgsqlTransaction)await db.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // Transaction-scoped, account-scoped arbitration. Locks have NO execution authority.
        // Command lock ensures same-ID races are compared, never failed by a unique error.
        // Intent lock serializes same intent racing across different aggregate identities.
        // Hash collisions only serialize unrelated work; database constraints remain authoritative.
        await LockIdAsync(db, tx, request.Scope, "Command", request.CommandId.Value, cancellationToken);
        await LockIdAsync(db, tx, request.Scope, "Intent", request.IntentId.Value, cancellationToken);

        var known = await ReadDigestAsync(db, tx, request.Scope, request.CommandId.Value,
            cancellationToken);
        if (known is not null)
        {
            var result = known.SequenceEqual(digest)
                ? OmsJournalWriteResult.Duplicate
                : OmsJournalWriteResult.Collision;
            if (result == OmsJournalWriteResult.Collision)
                await RecordIncidentAsync(db, tx, request.Scope, "CommandCollision",
                    request.CommandId.Value.ToString("D"), cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return result;
        }

        // Validate pre-existing intent before applying aggregate CAS.
        await using (var intentCheck = Command($"""
SELECT aggregate_id FROM oms_intents WHERE {ScopeWhere} AND intent_id=@intent
""", db, tx, request.Scope))
        {
            intentCheck.Parameters.AddWithValue("intent", request.IntentId.Value);
            var prior = await intentCheck.ExecuteScalarAsync(cancellationToken);
            if (prior is Guid other && other != request.LocalOrderId.Value)
            {
                await RecordIncidentAsync(db, tx, request.Scope, "IntentCollision",
                    request.IntentId.Value.ToString("D"), cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return OmsJournalWriteResult.Collision;
            }
        }

        await using (var seed = Command("""
INSERT INTO oms_aggregates(tenant_id,venue_id,environment,account_id,subaccount_id,aggregate_id)
VALUES (@tenant,@venue,@environment,@account,@subaccount,@aggregate)
ON CONFLICT DO NOTHING
""", db, tx, request.Scope))
        {
            seed.Parameters.AddWithValue("aggregate", request.LocalOrderId.Value);
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var intent = Command("""
INSERT INTO oms_intents
(tenant_id,venue_id,environment,account_id,subaccount_id,intent_id,aggregate_id,schema_version,provenance)
VALUES (@tenant,@venue,@environment,@account,@subaccount,@intent,@aggregate,@schema,@provenance)
ON CONFLICT DO NOTHING
""", db, tx, request.Scope))
        {
            intent.Parameters.AddWithValue("intent", request.IntentId.Value);
            intent.Parameters.AddWithValue("aggregate", request.LocalOrderId.Value);
            intent.Parameters.AddWithValue("schema", OmsContractSchema.Version);
            intent.Parameters.AddWithValue("provenance", request.Payload.Provenance);
            await intent.ExecuteNonQueryAsync(cancellationToken);
        }


        // Conflict arbitration on intent unique key has completed; re-read its true
        // owning aggregate AFTER insert, not only from an earlier racing snapshot.
        await using (var verifyIntent = Command($"""
SELECT aggregate_id FROM oms_intents
WHERE {ScopeWhere} AND intent_id=@intent FOR UPDATE
""", db, tx, request.Scope))
        {
            verifyIntent.Parameters.AddWithValue("intent", request.IntentId.Value);
            var linked = await verifyIntent.ExecuteScalarAsync(cancellationToken);
            if (linked is not Guid owner || owner != request.LocalOrderId.Value)
                throw new InvalidOperationException(
                    "IntentId belongs to another aggregate; transaction rolled back.");
        }

        long nextVersion;
        await using (var cas = Command($"""
UPDATE oms_aggregates SET version=version+1
WHERE {ScopeWhere} AND aggregate_id=@aggregate AND version=@expected
RETURNING version
""", db, tx, request.Scope))
        {
            cas.Parameters.AddWithValue("aggregate", request.LocalOrderId.Value);
            cas.Parameters.AddWithValue("expected", request.ExpectedAggregateVersion.Value);
            var value = await cas.ExecuteScalarAsync(cancellationToken);
            if (value is null)
            {
                // A concurrent identical request may have won while we waited for CAS.
                // ReadCommitted sees the committed winner after the conflicting UPDATE.
                var raced = await ReadDigestAsync(db, tx, request.Scope,
                    request.CommandId.Value, cancellationToken);
                if (raced is not null)
                {
                    var result = raced.SequenceEqual(digest)
                        ? OmsJournalWriteResult.Duplicate : OmsJournalWriteResult.Collision;
                    if (result == OmsJournalWriteResult.Collision)
                        await RecordIncidentAsync(db, tx, request.Scope, "CommandCollision",
                            request.CommandId.Value.ToString("D"), cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    return result;
                }
                // Any tentative aggregate/intent creation must roll back on stale CAS.
                return OmsJournalWriteResult.StaleVersion;
            }
            nextVersion = (long)value;
        }


        await using (var write = Command("""
INSERT INTO oms_commands
(tenant_id,venue_id,environment,account_id,subaccount_id,command_id,intent_id,
 aggregate_id,expected_version,aggregate_version,schema_version,command_kind,
 payload_sha256,request_json,dispatch_phase)
VALUES (@tenant,@venue,@environment,@account,@subaccount,@command,@intent,
 @aggregate,@expected,@version,@schema,@kind,@digest,@json,'PersistedNotSent')
""", db, tx, request.Scope))
        {
            write.Parameters.AddWithValue("command", request.CommandId.Value);
            write.Parameters.AddWithValue("intent", request.IntentId.Value);
            write.Parameters.AddWithValue("aggregate", request.LocalOrderId.Value);
            write.Parameters.AddWithValue("expected", request.ExpectedAggregateVersion.Value);
            write.Parameters.AddWithValue("version", nextVersion);
            write.Parameters.AddWithValue("schema", OmsContractSchema.Version);
            write.Parameters.AddWithValue("kind", request.Payload.Kind.ToString());
            AddJson(write, json, digest);
            await write.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var outbox = Command("""
INSERT INTO oms_outbox(tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
VALUES (@tenant,@venue,@environment,@account,@subaccount,@command)
""", db, tx, request.Scope))
        {
            outbox.Parameters.AddWithValue("command", request.CommandId.Value);
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }

        if (fault is not null) await fault(OmsJournalFailPoint.BeforeCommit);
        await tx.CommitAsync(cancellationToken);
        return OmsJournalWriteResult.Created;
    }

    /// <summary>
    /// Commit dispatch_started and a durable attempt before any future physical send.
    /// This method does NOT send. After this commit the outbox is no longer safely dispatchable.
    /// </summary>
    public async Task<OmsJournalAttemptResult> MarkDispatchStartedAsync(
        AccountKey scope, OmsCommandId commandId, CancellationToken cancellationToken = default)
    {
        await using var db = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = (NpgsqlTransaction)await db.BeginTransactionAsync(cancellationToken);
        int affected;
        await using (var update = Command($"""
UPDATE oms_commands SET dispatch_phase='DispatchStarted',updated_at=clock_timestamp()
WHERE {ScopeWhere} AND command_id=@command AND dispatch_phase='PersistedNotSent'
""", db, tx, scope))
        {
            update.Parameters.AddWithValue("command", commandId.Value);
            affected = await update.ExecuteNonQueryAsync(cancellationToken);
        }
        if (affected == 0)
        {
            bool exists;
            await using (var check = Command($"""
SELECT 1 FROM oms_commands WHERE {ScopeWhere} AND command_id=@command
""", db, tx, scope))
            {
                check.Parameters.AddWithValue("command", commandId.Value);
                exists = await check.ExecuteScalarAsync(cancellationToken) is not null;
            }
            await tx.CommitAsync(cancellationToken);
            return exists ? OmsJournalAttemptResult.AlreadyStartedOrResolved
                : OmsJournalAttemptResult.NotFound;
        }
        await using (var attempt = Command("""
INSERT INTO oms_attempts
(tenant_id,venue_id,environment,account_id,subaccount_id,command_id,attempt_number,dispatch_phase)
VALUES (@tenant,@venue,@environment,@account,@subaccount,@command,1,'DispatchStarted')
""", db, tx, scope))
        {
            attempt.Parameters.AddWithValue("command", commandId.Value);
            await attempt.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var outbox = Command($"""
UPDATE oms_outbox SET state='NeedsRecovery',changed_at=clock_timestamp()
WHERE {ScopeWhere} AND command_id=@command AND state='NotSent'
""", db, tx, scope))
        {
            outbox.Parameters.AddWithValue("command", commandId.Value);
            if (await outbox.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Missing outbox row: refusing to advance attempt.");
        }
        await tx.CommitAsync(cancellationToken);
        return OmsJournalAttemptResult.Started;
    }

    /// <summary>Records a known outcome; ACK is only transport acknowledgement, not a fill.</summary>
    public async Task<bool> RecordOutcomeAsync(AccountKey scope, OmsCommandId commandId,
        OmsJournalDispatchOutcome outcome, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        var phase = outcome.ToString();
        await using var db = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = (NpgsqlTransaction)await db.BeginTransactionAsync(cancellationToken);
        await using (var update = Command($"""
UPDATE oms_commands SET dispatch_phase=@phase,updated_at=clock_timestamp()
WHERE {ScopeWhere} AND command_id=@command AND dispatch_phase IN ('DispatchStarted','Ambiguous')
""", db, tx, scope))
        {
            update.Parameters.AddWithValue("command", commandId.Value);
            update.Parameters.AddWithValue("phase", phase);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
                return false;
        }
        await using (var attempt = Command($"""
UPDATE oms_attempts SET dispatch_phase=@phase,updated_at=clock_timestamp()
WHERE {ScopeWhere} AND command_id=@command AND attempt_number=1
""", db, tx, scope))
        {
            attempt.Parameters.AddWithValue("command", commandId.Value);
            attempt.Parameters.AddWithValue("phase", phase);
            if (await attempt.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Committed send has no durable attempt.");
        }
        await using (var outbox = Command($"""
UPDATE oms_outbox SET state=@state,changed_at=clock_timestamp()
WHERE {ScopeWhere} AND command_id=@command
""", db, tx, scope))
        {
            outbox.Parameters.AddWithValue("command", commandId.Value);
            outbox.Parameters.AddWithValue("state",
                outcome == OmsJournalDispatchOutcome.Ambiguous ? "NeedsRecovery" : "Resolved");
            if (await outbox.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Committed send has no outbox.");
        }
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Store-only normalized private event; event namespace must reflect venue-specific
    /// reset/session semantics. Conflicting payload with same ID is quarantined.
    /// </summary>
    public async Task<OmsJournalInboxResult> PersistInboxAsync(
        OmsJournalInboxEvent item, CancellationToken cancellationToken = default)
    {
        var (json, digest) = OmsJournalCanonical.EncodeInbox(item);
        await using var db = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = (NpgsqlTransaction)await db.BeginTransactionAsync(cancellationToken);
        int inserted;
        await using (var insert = Command("""
INSERT INTO oms_inbox
(tenant_id,venue_id,environment,account_id,subaccount_id,event_namespace,
 event_id,event_kind,aggregate_id,schema_version,payload_sha256,event_json)
VALUES (@tenant,@venue,@environment,@account,@subaccount,@namespace,
 @event,@kind,@aggregate,@schema,@digest,@json)
ON CONFLICT DO NOTHING
""", db, tx, item.Scope))
        {
            insert.Parameters.AddWithValue("namespace", item.Namespace);
            insert.Parameters.AddWithValue("event", item.EventId);
            insert.Parameters.AddWithValue("kind", item.Kind.ToString());
            insert.Parameters.Add(new NpgsqlParameter("aggregate", NpgsqlDbType.Uuid)
            { Value = (object?)item.LocalOrderId?.Value ?? DBNull.Value });
            insert.Parameters.AddWithValue("schema", OmsContractSchema.Version);
            AddJson(insert, json, digest);
            inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        if (inserted == 1)
        {
            await tx.CommitAsync(cancellationToken);
            return OmsJournalInboxResult.Inserted;
        }
        byte[] stored;
        await using (var existing = Command($"""
SELECT payload_sha256 FROM oms_inbox
WHERE {ScopeWhere} AND event_namespace=@namespace AND event_id=@event
FOR UPDATE
""", db, tx, item.Scope))
        {
            existing.Parameters.AddWithValue("namespace", item.Namespace);
            existing.Parameters.AddWithValue("event", item.EventId);
            stored = (byte[]?)await existing.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Conflicting inbox row disappeared.");
        }
        if (stored.SequenceEqual(digest))
        {
            await tx.CommitAsync(cancellationToken);
            return OmsJournalInboxResult.Duplicate;
        }
        await RecordIncidentAsync(db, tx, item.Scope, "EventCollision",
            item.Namespace + ":" + item.EventId, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return OmsJournalInboxResult.Collision;
    }

    /// <summary>Lease metadata only. NOT an authorization for physical sends (requires sole gateway/fencing).</summary>
    public async Task<long?> TryAcquireMetadataLeaseAsync(AccountKey scope, Guid ownerId,
        TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty || duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
            throw new ArgumentException("Owner and lease duration must be valid.");
        await using var db = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = Command("""
INSERT INTO oms_account_leases
(tenant_id,venue_id,environment,account_id,subaccount_id,generation,owner_id,expires_at)
VALUES (@tenant,@venue,@environment,@account,@subaccount,1,@owner,
 clock_timestamp()+make_interval(secs => @seconds))
ON CONFLICT (tenant_id,venue_id,environment,account_id,subaccount_id)
DO UPDATE SET generation=oms_account_leases.generation+1,
 owner_id=EXCLUDED.owner_id, expires_at=EXCLUDED.expires_at
WHERE oms_account_leases.expires_at < clock_timestamp()
RETURNING generation
""", db, null, scope);
        cmd.Parameters.AddWithValue("owner", ownerId);
        cmd.Parameters.AddWithValue("seconds", duration.TotalSeconds);
        return (long?)await cmd.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task LockIdAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        AccountKey scope, string role, Guid id, CancellationToken ct)
    {
        var lockScope = JsonSerializer.Serialize(new[]
        {
            "OMS-B2-v1", role, scope.Tenant.Value, scope.Venue.Value,
            ((int)scope.Environment).ToString(System.Globalization.CultureInfo.InvariantCulture),
            scope.Account.Value, scope.Subaccount?.Value ?? "", id.ToString("N")
        });
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lockScope));
        var lockId = BinaryPrimitives.ReadInt64BigEndian(hash.AsSpan(0, sizeof(long)));
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@lock_key)", db, tx);
        command.Parameters.AddWithValue("lock_key", lockId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<byte[]?> ReadDigestAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        AccountKey scope, Guid commandId, CancellationToken ct)
    {
        await using var cmd = Command($"""
SELECT payload_sha256 FROM oms_commands
WHERE {ScopeWhere} AND command_id=@command
FOR UPDATE
""", db, tx, scope);
        cmd.Parameters.AddWithValue("command", commandId);
        return (byte[]?)await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task RecordIncidentAsync(NpgsqlConnection db, NpgsqlTransaction tx,
        AccountKey scope, string kind, string correlation, CancellationToken ct)
    {
        await using var cmd = Command("""
INSERT INTO oms_integrity_incidents
(tenant_id,venue_id,environment,account_id,subaccount_id,kind,correlation)
VALUES (@tenant,@venue,@environment,@account,@subaccount,@kind,@correlation)
""", db, tx, scope);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("correlation", correlation.Length > 160
            ? correlation[..160] : correlation);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
