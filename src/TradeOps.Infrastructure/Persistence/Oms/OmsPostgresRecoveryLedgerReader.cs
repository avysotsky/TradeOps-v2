using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>
/// Bounded account-wide command/attempt/outbox and private-event snapshot. SELECT only;
/// journal v1 and private event v2 hashes retain their original canonical encodings.
/// Does not grant privileges, migrate, repair, dispatch, or publish a recovered projection.
/// </summary>
public sealed class OmsPostgresRecoveryLedgerReader(NpgsqlDataSource source) : IOmsRecoveryLedgerReader
{
    private const string ScopeWhere = """
c.tenant_id=@tenant AND c.venue_id=@venue AND c.environment=@environment
 AND c.account_id=@account AND c.subaccount_id=@subaccount
""";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async Task<OmsRecoveryLedgerSnapshot> ReadAsync(AccountKey scope, int maximumRows = 10000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Venue.Value != "DERIBIT" || scope.Environment is not (OmsEnvironment.Mainnet or OmsEnvironment.Testnet))
            throw new ArgumentException("A trusted Deribit account scope is required.");
        if (maximumRows is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        await using var db = await source.OpenConnectionAsync(cancellationToken);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await using (var mode = new NpgsqlCommand("SET TRANSACTION READ ONLY", db, tx))
            await mode.ExecuteNonQueryAsync(cancellationToken);
        DateTimeOffset readAt;
        await using (var stamp = new NpgsqlCommand("SELECT transaction_timestamp()", db, tx))
            readAt = new((DateTime)(await stamp.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Snapshot timestamp unavailable.")));
        var snapshotId = Guid.NewGuid();
        // Establish and consume the private side in exactly the same MVCC transaction.
        var retained = await OmsPostgresPrivateRecoveryReader.ReadSnapshotAsync(db, tx, scope,
            maximumRows, snapshotId, readAt, cancellationToken);
        var commands = new List<OmsRecoveryCommand>();
        var problems = new HashSet<OmsRecoveryLedgerProblem>();
        var aggregateVersions = new Dictionary<OmsLocalOrderId, long>();
        var bytes = 0;
        try
        {
            await using var command = new NpgsqlCommand($"""
SELECT c.command_id,c.intent_id,c.aggregate_id,c.expected_version,c.aggregate_version,
 c.schema_version,c.command_kind,c.payload_sha256,
 CASE WHEN octet_length(c.request_json::text)<=16384 THEN c.request_json::text END,
 c.dispatch_phase,o.state,a.n,a.number,a.phase,
 i.aggregate_id,i.schema_version,i.provenance,g.version,
 c.created_at,c.updated_at,a.started,a.updated
FROM oms_commands c
LEFT JOIN oms_outbox o USING (tenant_id,venue_id,environment,account_id,subaccount_id,command_id)
LEFT JOIN oms_intents i ON (i.tenant_id,i.venue_id,i.environment,i.account_id,i.subaccount_id,i.intent_id)=
 (c.tenant_id,c.venue_id,c.environment,c.account_id,c.subaccount_id,c.intent_id)
LEFT JOIN oms_aggregates g ON (g.tenant_id,g.venue_id,g.environment,g.account_id,g.subaccount_id,g.aggregate_id)=
 (c.tenant_id,c.venue_id,c.environment,c.account_id,c.subaccount_id,c.aggregate_id)
LEFT JOIN LATERAL (
 SELECT count(*) n,min(attempt_number) number,min(dispatch_phase) phase,
 min(started_at) started,max(updated_at) updated
 FROM oms_attempts t WHERE (t.tenant_id,t.venue_id,t.environment,t.account_id,t.subaccount_id,t.command_id)=
 (c.tenant_id,c.venue_id,c.environment,c.account_id,c.subaccount_id,c.command_id)
) a ON true
WHERE {ScopeWhere} ORDER BY c.aggregate_id,c.aggregate_version LIMIT @limit
""", db, tx);
            Bind(command);
            await using (var rows = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await rows.ReadAsync(cancellationToken))
                {
                    if (commands.Count >= maximumRows || rows.IsDBNull(8))
                        throw new LedgerFailure(OmsRecoveryLedgerProblem.CapacityExceeded);
                    if (rows.GetInt32(5) != OmsContractSchema.Version)
                        throw new LedgerFailure(OmsRecoveryLedgerProblem.UnsupportedSchema);
                    var raw = rows.GetString(8);
                    bytes = checked(bytes + Encoding.UTF8.GetByteCount(raw));
                    if (bytes > 16 * 1024 * 1024) throw new LedgerFailure(OmsRecoveryLedgerProblem.CapacityExceeded);
                    using var doc = JsonDocument.Parse(raw);
                    var root = doc.RootElement;
                    if (root.GetProperty("schemaVersion").GetInt32() != OmsContractSchema.Version)
                        throw new LedgerFailure(OmsRecoveryLedgerProblem.UnsupportedSchema);
                    var request = new OmsJournalCommandRequest(scope, new(rows.GetGuid(2)), new(rows.GetGuid(1)),
                        new(rows.GetGuid(0)), new(rows.GetInt64(3)),
                        root.GetProperty("command").Deserialize<OmsJournalCommandPayload>(Json)
                            ?? throw new ArgumentException("Missing command payload."));
                    var encoded = OmsJournalCanonical.EncodeCommand(request);
                    using var canonical = JsonDocument.Parse(encoded.Json);
                    if (!JsonElement.DeepEquals(root, canonical.RootElement) ||
                        !CryptographicOperations.FixedTimeEquals(encoded.Sha256, rows.GetFieldValue<byte[]>(7)) ||
                        rows.GetString(6) != request.Payload.Kind.ToString() || rows.IsDBNull(10) ||
                        rows.IsDBNull(14) || rows.GetGuid(14) != request.LocalOrderId.Value ||
                        rows.GetInt32(15) != OmsContractSchema.Version || rows.GetString(16) != request.Payload.Provenance ||
                        rows.IsDBNull(17))
                        throw new ArgumentException("Invalid command payload or journal links.");
                    var phase = Parse<OmsRecoveryDispatchPhase>(rows.GetString(9));
                    var attempts = checked((int)rows.GetInt64(11));
                    var item = new OmsRecoveryCommand(request, rows.GetInt64(4), phase,
                        Parse<OmsRecoveryOutboxState>(rows.GetString(10)), attempts,
                        rows.IsDBNull(13) ? null : Parse<OmsRecoveryDispatchPhase>(rows.GetString(13)));
                    item.Validate(scope);
                    if (attempts != 0 && (rows.GetInt32(12) != 1 || rows.IsDBNull(20) || rows.IsDBNull(21) ||
                        rows.GetDateTime(20) < rows.GetDateTime(18) || rows.GetDateTime(21) < rows.GetDateTime(20) ||
                        rows.GetDateTime(21) > readAt.UtcDateTime) ||
                        rows.GetDateTime(19) < rows.GetDateTime(18) || rows.GetDateTime(19) > readAt.UtcDateTime)
                        throw new ArgumentException("Invalid dispatch chronology.");
                    aggregateVersions[request.LocalOrderId] = rows.GetInt64(17);
                    commands.Add(item);
                }
            }
            foreach (var group in commands.GroupBy(x => x.Request.LocalOrderId))
                if (aggregateVersions[group.Key] != group.Count() || !group.Select(x => x.AggregateVersion)
                    .Order().SequenceEqual(Enumerable.Range(1, group.Count()).Select(x => (long)x)))
                    throw new ArgumentException("Incomplete aggregate command chain.");
            await using var orphans = new NpgsqlCommand($"""
SELECT EXISTS(SELECT 1 FROM oms_intents c WHERE {ScopeWhere} AND NOT EXISTS(
 SELECT 1 FROM oms_commands d WHERE (d.tenant_id,d.venue_id,d.environment,d.account_id,d.subaccount_id,d.intent_id)=
 (c.tenant_id,c.venue_id,c.environment,c.account_id,c.subaccount_id,c.intent_id)))
 OR EXISTS(SELECT 1 FROM oms_aggregates c WHERE {ScopeWhere} AND c.version>0 AND NOT EXISTS(
 SELECT 1 FROM oms_commands d WHERE (d.tenant_id,d.venue_id,d.environment,d.account_id,d.subaccount_id,d.aggregate_id)=
 (c.tenant_id,c.venue_id,c.environment,c.account_id,c.subaccount_id,c.aggregate_id)))
""", db, tx);
            Bind(orphans);
            if (await orphans.ExecuteScalarAsync(cancellationToken) is true)
                throw new ArgumentException("Orphaned intent or versioned aggregate.");
        }
        catch (LedgerFailure failure) { problems.Add(failure.Problem); commands.Clear(); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException or KeyNotFoundException)
        { problems.Add(OmsRecoveryLedgerProblem.InvalidRecord); commands.Clear(); }
        await tx.CommitAsync(cancellationToken);
        return new(retained, commands.AsReadOnly(), problems.Order().ToArray());

        void Bind(NpgsqlCommand command)
        {
            command.Parameters.AddWithValue("tenant", scope.Tenant.Value);
            command.Parameters.AddWithValue("venue", scope.Venue.Value);
            command.Parameters.AddWithValue("environment", (short)scope.Environment);
            command.Parameters.AddWithValue("account", scope.Account.Value);
            command.Parameters.AddWithValue("subaccount", scope.Subaccount?.Value ?? "");
            command.Parameters.AddWithValue("limit", maximumRows + 1);
        }
    }

    private static T Parse<T>(string raw) where T : struct, Enum =>
        Enum.TryParse<T>(raw, out var value) && Enum.IsDefined(value) && value.ToString() == raw
            ? value : throw new ArgumentException("Unsupported journal phase.");

    private sealed class LedgerFailure(OmsRecoveryLedgerProblem problem) : Exception
    { public OmsRecoveryLedgerProblem Problem { get; } = problem; }
}
