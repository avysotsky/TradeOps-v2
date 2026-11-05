using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>
/// Append-only response evidence, no sender/phase repair/DI registration. Caller is the trusted
/// order-entry path, not a recovery lookup or an arbitrary label-to-command importer.
/// Locks arbitrate evidence writes only; they provide no physical-send authority.
/// </summary>
public sealed class OmsPostgresDeribitCommandReceiptJournal(NpgsqlDataSource source) : IDeribitCommandReceiptJournal
{
    private const string Where = """
tenant_id=@tenant AND venue_id=@venue AND environment=@environment AND account_id=@account AND subaccount_id=@subaccount
""";

    public Task<DeribitCommandReceiptWriteResult> RecordAsync(DeribitJsonRpcCommandReceipt receipt,
        CancellationToken cancellationToken = default) => RecordWithFaultAsync(receipt, null, cancellationToken);

    public async Task<DeribitCommandReceiptWriteResult> RecordWithFaultAsync(DeribitJsonRpcCommandReceipt receipt,
        Func<OmsJournalFailPoint, Task>? fault, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var encoded = DeribitCommandReceiptCanonical.Encode(receipt);
        var request = receipt.Command;
        await using var db = await source.OpenConnectionAsync(cancellationToken);
        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        // One account-wide lock also arbitrates request-ID reuse across different native orders.
        var arbitration = JsonSerializer.Serialize(new[] { "Deribit-command-receipt-v1", request.Scope.Tenant.Value,
            request.Scope.Venue.Value, ((int)request.Scope.Environment).ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.Scope.Account.Value, request.Scope.Subaccount?.Value ?? "" });
        await using (var lease = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", db, tx))
        {
            lease.Parameters.AddWithValue("key", BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(arbitration))));
            await lease.ExecuteNonQueryAsync(cancellationToken);
        }
        byte[] commandDigest;
        string phase;
        await using (var command = Sql($"""
SELECT CASE WHEN octet_length(c.request_json::text)<=16384 THEN c.request_json::text END,
 c.payload_sha256,c.dispatch_phase,t.dispatch_phase,t.started_at,
 c.aggregate_id,c.intent_id,c.expected_version,c.aggregate_version,c.schema_version,c.command_kind,
 o.state,clock_timestamp(),t.attempt_number
FROM oms_commands c
LEFT JOIN oms_attempts t USING (tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
LEFT JOIN oms_outbox o USING (tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
WHERE c.tenant_id=@tenant AND c.venue_id=@venue AND c.environment=@environment
 AND c.account_id=@account AND c.subaccount_id=@subaccount AND c.command_id=@command
FOR UPDATE OF c
"""))
        await using (var rows = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await rows.ReadAsync(cancellationToken)) return DeribitCommandReceiptWriteResult.NotEligible;
            phase = rows.GetString(2);
            if (rows.IsDBNull(0) || phase is not ("DispatchStarted" or "Ambiguous" or "Acknowledged") || rows.IsDBNull(3) ||
                rows.GetInt32(13) != 1 ||
                rows.GetString(3) != phase || rows.IsDBNull(11) ||
                rows.GetString(11) != (phase == "Acknowledged" ? "Resolved" : "NeedsRecovery") ||
                rows.GetGuid(5) != request.LocalOrderId.Value || rows.GetGuid(6) != request.IntentId.Value ||
                rows.GetInt64(7) != request.ExpectedAggregateVersion.Value || rows.GetInt64(8) != checked(request.ExpectedAggregateVersion.Value + 1) ||
                rows.GetInt32(9) != OmsContractSchema.Version || rows.GetString(10) != request.Payload.Kind.ToString() ||
                receipt.SentAtUtc < new DateTimeOffset(rows.GetDateTime(4)).AddSeconds(-5) ||
                receipt.ReceivedAtUtc > new DateTimeOffset(rows.GetDateTime(12)).AddSeconds(5))
                return DeribitCommandReceiptWriteResult.NotEligible;
            using var stored = JsonDocument.Parse(rows.GetString(0));
            using var supplied = JsonDocument.Parse(OmsJournalCanonical.EncodeCommand(request).Json);
            // Preserve journal v1's original decimal scale/hash while comparing semantic replay terms.
            var storedPayload = stored.RootElement.GetProperty("command").Deserialize<OmsJournalCommandPayload>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var canonicalStored = OmsJournalCanonical.EncodeCommand(request with { Payload = storedPayload });
            commandDigest = rows.GetFieldValue<byte[]>(1);
            if (!JsonElement.DeepEquals(stored.RootElement, supplied.RootElement) ||
                !CryptographicOperations.FixedTimeEquals(commandDigest, canonicalStored.Sha256) ||
                await rows.ReadAsync(cancellationToken))
                return DeribitCommandReceiptWriteResult.NotEligible;
        }

        await using (var prior = Sql($"SELECT receipt_sha256 FROM oms_deribit_command_receipts_v1 WHERE {Where} AND command_id=@command"))
        {
            if (await prior.ExecuteScalarAsync(cancellationToken) is byte[] digest)
            {
                if (CryptographicOperations.FixedTimeEquals(digest, encoded.Sha256))
                    return DeribitCommandReceiptWriteResult.Duplicate;
                return await Collision();
            }
        }
        await using (var conflict = Sql($"""
SELECT EXISTS(SELECT 1 FROM oms_deribit_command_receipts_v1 WHERE {Where} AND
 ((session_id=@session AND rpc_request_id=@rpc) OR
  (@kind='Place' AND command_kind='Place' AND currency=@currency AND native_order_id=@native)))
"""))
            if (await conflict.ExecuteScalarAsync(cancellationToken) is true) return await Collision();
        if (request.Payload.Kind == OmsJournalCommandKind.Cancel)
        {
            await using var placement = Sql("""
SELECT CASE WHEN octet_length(p.receipt_json::text)<=32768 THEN p.receipt_json::text END,p.receipt_sha256,
 CASE WHEN octet_length(c.request_json::text)<=16384 THEN c.request_json::text END,c.payload_sha256,
 c.command_id,c.aggregate_id,c.expected_version,c.dispatch_phase,p.command_sha256,p.aggregate_id
FROM oms_deribit_command_receipts_v1 p JOIN oms_commands c
 USING (tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
WHERE p.tenant_id=@tenant AND p.venue_id=@venue AND p.environment=@environment
 AND p.account_id=@account AND p.subaccount_id=@subaccount AND p.command_kind='Place'
 AND p.aggregate_id=@aggregate AND p.currency=@currency AND p.native_order_id=@native
 AND p.native_instrument=@instrument
""");
            await using var parent = await placement.ExecuteReaderAsync(cancellationToken);
            if (!await parent.ReadAsync(cancellationToken) || parent.IsDBNull(0) || parent.IsDBNull(2))
                return DeribitCommandReceiptWriteResult.NotEligible;
            var parentCommand = OmsJournalRecoveryCommandCodec.Decode(parent.GetString(2), request.Scope);
            var parentReceipt = DeribitCommandReceiptCanonical.Decode(parent.GetString(0), parentCommand);
            if (parentCommand.CommandId.Value != parent.GetGuid(4) || parentCommand.LocalOrderId.Value != parent.GetGuid(5) ||
                parentCommand.ExpectedAggregateVersion.Value != parent.GetInt64(6) ||
                parent.GetString(7) is not ("DispatchStarted" or "Ambiguous" or "Acknowledged") ||
                parent.GetGuid(9) != parentCommand.LocalOrderId.Value ||
                !CryptographicOperations.FixedTimeEquals(DeribitCommandReceiptCanonical.Encode(parentReceipt).Sha256, parent.GetFieldValue<byte[]>(1)) ||
                !CryptographicOperations.FixedTimeEquals(OmsJournalCanonical.EncodeCommand(parentCommand).Sha256, parent.GetFieldValue<byte[]>(3)) ||
                !CryptographicOperations.FixedTimeEquals(parent.GetFieldValue<byte[]>(8), parent.GetFieldValue<byte[]>(3)))
                return DeribitCommandReceiptWriteResult.NotEligible;
            DeribitCommandReceiptSet.ValidateCancelBinding(receipt, parentReceipt);
        }
        await using (var write = Sql("""
INSERT INTO oms_deribit_command_receipts_v1
 (tenant_id,venue_id,environment,account_id,subaccount_id,command_id,aggregate_id,attempt_number,schema_version,
 command_kind,session_id,rpc_request_id,currency,native_order_id,native_instrument,command_sha256,receipt_sha256,receipt_json)
VALUES (@tenant,@venue,@environment,@account,@subaccount,@command,@aggregate,1,1,
 @kind,@session,@rpc,@currency,@native,@instrument,@commandDigest,@receiptDigest,@json)
"""))
        {
            write.Parameters.AddWithValue("commandDigest", commandDigest);
            write.Parameters.AddWithValue("receiptDigest", encoded.Sha256);
            write.Parameters.Add(new NpgsqlParameter("json", NpgsqlDbType.Jsonb) { Value = encoded.Json });
            await write.ExecuteNonQueryAsync(cancellationToken);
        }
        if (fault is not null) await fault(OmsJournalFailPoint.BeforeCommit);
        await tx.CommitAsync(cancellationToken);
        return DeribitCommandReceiptWriteResult.Inserted;

        NpgsqlCommand Sql(string text)
        {
            var command = new NpgsqlCommand(text, db, tx);
            Bind(command, request.Scope);
            command.Parameters.AddWithValue("command", request.CommandId.Value);
            command.Parameters.AddWithValue("aggregate", request.LocalOrderId.Value);
            command.Parameters.AddWithValue("kind", request.Payload.Kind.ToString());
            command.Parameters.AddWithValue("session", receipt.SessionId);
            command.Parameters.AddWithValue("rpc", receipt.RpcRequestId);
            command.Parameters.AddWithValue("currency", receipt.Order.Currency);
            command.Parameters.AddWithValue("native", receipt.Order.ExchangeOrderId);
            command.Parameters.AddWithValue("instrument", receipt.Order.NativeInstrument);
            return command;
        }
        async Task<DeribitCommandReceiptWriteResult> Collision()
        {
            await using var incident = Sql("""
INSERT INTO oms_integrity_incidents(tenant_id,venue_id,environment,account_id,subaccount_id,kind,correlation)
VALUES (@tenant,@venue,@environment,@account,@subaccount,'CommandCollision',@correlation)
""");
            incident.Parameters.AddWithValue("correlation", "deribit-receipt-v1:" + request.CommandId.Value.ToString("D"));
            await incident.ExecuteNonQueryAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return DeribitCommandReceiptWriteResult.Collision;
        }
    }

    internal static void Bind(NpgsqlCommand command, AccountKey scope)
    {
        command.Parameters.AddWithValue("tenant", scope.Tenant.Value);
        command.Parameters.AddWithValue("venue", scope.Venue.Value);
        command.Parameters.AddWithValue("environment", (short)scope.Environment);
        command.Parameters.AddWithValue("account", scope.Account.Value);
        command.Parameters.AddWithValue("subaccount", scope.Subaccount?.Value ?? "");
    }
}
