using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>
/// Full bounded REPEATABLE READ / READ ONLY snapshot. No timestamp/identity high-water
/// mark: allocation and recorded_at do not establish transaction commit ordering.
/// No migrations, writes, locks on capture rows, or automatic host registration.
/// </summary>
public sealed class OmsPostgresPrivateRecoveryReader(NpgsqlDataSource source) : IOmsPrivateRecoveryReader
{
    private const int MaximumJsonBytes = 131072;
    private const int MaximumBatchBytes = 16 * 1024 * 1024;
    private const string ScopeWhere = """
tenant_id=@tenant AND venue_id=@venue AND environment=@environment
 AND account_id=@account AND subaccount_id=@subaccount
""";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async Task<OmsPrivateRecoveryReadResult> ReadAsync(AccountKey scope, int maximumRows = 10000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Venue.Value != "DERIBIT" || scope.Environment is not (OmsEnvironment.Mainnet or OmsEnvironment.Testnet))
            throw new ArgumentException("A trusted Deribit account scope is required.");
        if (maximumRows is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maximumRows));
        var snapshot = Guid.NewGuid();
        var problems = new HashSet<OmsPrivateRecoveryReadProblem>();
        var observations = new List<OmsPrivateRecoveryObservation>();
        var inbox = new Dictionary<(string Namespace, string Id), InboxRow>();
        var witnessed = new HashSet<(string Namespace, string Id)>();
        var bytes = 0;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await using (var mode = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, tx))
            await mode.ExecuteNonQueryAsync(cancellationToken);
        DateTimeOffset readAt;
        await using (var time = new NpgsqlCommand("SELECT transaction_timestamp()", connection, tx))
        {
            if (await time.ExecuteScalarAsync(cancellationToken) is not DateTime instant || instant.Kind != DateTimeKind.Utc)
                throw new InvalidOperationException("Recovery snapshot timestamp unavailable.");
            readAt = new(instant);
        }
        try
        {
            await using (var command = Command($"""
SELECT event_namespace,event_id,event_kind,schema_version,payload_sha256,
 CASE WHEN octet_length(event_json::text)<={MaximumJsonBytes} THEN event_json::text END,aggregate_id
FROM oms_inbox WHERE {ScopeWhere} ORDER BY event_namespace,event_id LIMIT @limit
"""))
            await using (var rows = await command.ExecuteReaderAsync(cancellationToken))
            {
                var count = 0;
                while (await rows.ReadAsync(cancellationToken))
                {
                    if (++count > maximumRows) Fail(OmsPrivateRecoveryReadProblem.CapacityExceeded);
                    if (rows.GetInt32(3) != 2) Fail(OmsPrivateRecoveryReadProblem.UnsupportedSchema);
                    if (!rows.IsDBNull(6)) Fail(OmsPrivateRecoveryReadProblem.InvalidRecord);
                    var raw = Payload(rows, 5);
                    var facts = DecodeEvent(raw, scope);
                    inbox.Add((rows.GetString(0), rows.GetString(1)),
                        new(facts, rows.GetString(2), rows.GetFieldValue<byte[]>(4)));
                }
            }
            await using (var command = Command($"""
SELECT capture_id,stream_epoch,schema_version,event_namespace,event_id,disposition,observation_sha256,
 CASE WHEN octet_length(observation_json::text)<={MaximumJsonBytes} THEN observation_json::text END
FROM oms_private_capture_observations WHERE {ScopeWhere}
ORDER BY capture_id,observation_sha256 LIMIT @limit
"""))
            await using (var rows = await command.ExecuteReaderAsync(cancellationToken))
            {
                var count = 0;
                while (await rows.ReadAsync(cancellationToken))
                {
                    if (++count > maximumRows) Fail(OmsPrivateRecoveryReadProblem.CapacityExceeded);
                    if (rows.GetInt32(2) != 1) Fail(OmsPrivateRecoveryReadProblem.UnsupportedSchema);
                    using var doc = JsonDocument.Parse(Payload(rows, 7));
                    var root = doc.RootElement;
                    Fields(root, "captureSchemaVersion", "eventNamespace", "eventId", "venueEvent", "observation");
                    if (root.GetProperty("captureSchemaVersion").GetInt32() != 1)
                        Fail(OmsPrivateRecoveryReadProblem.UnsupportedSchema);
                    var facts = DecodeEvent(root.GetProperty("venueEvent"), scope);
                    var receiptJson = root.GetProperty("observation");
                    Fields(receiptJson, "captureId", "streamEpoch", "receivedAtUtc", "source", "channel");
                    var receipt = receiptJson.Deserialize<OmsPrivateObservation>(Json)
                        ?? throw new ReadFailure(OmsPrivateRecoveryReadProblem.InvalidRecord);
                    var envelope = new OmsPrivateEventEnvelope(scope, facts, receipt);
                    var encoded = OmsPrivateEventCanonical.Encode(envelope);
                    var observation = OmsPrivateEventCanonical.EncodeObservation(envelope, encoded);
                    if (receipt.CaptureId != rows.GetGuid(0) || receipt.StreamEpoch != rows.GetGuid(1) ||
                        encoded.Namespace != rows.GetString(3) || encoded.EventId != rows.GetString(4) ||
                        root.GetProperty("eventNamespace").GetString() != encoded.Namespace ||
                        root.GetProperty("eventId").GetString() != encoded.EventId ||
                        !CryptographicOperations.FixedTimeEquals(observation.Sha256, rows.GetFieldValue<byte[]>(6)))
                        Fail(OmsPrivateRecoveryReadProblem.InvalidRecord);
                    var disposition = (OmsPrivateCaptureResult)rows.GetInt16(5);
                    if (!Enum.IsDefined(disposition)) Fail(OmsPrivateRecoveryReadProblem.InvalidRecord);
                    if (disposition == OmsPrivateCaptureResult.Collision)
                        problems.Add(OmsPrivateRecoveryReadProblem.QuarantinedObservation);
                    else
                    {
                        var key = (encoded.Namespace, encoded.EventId);
                        if (!inbox.TryGetValue(key, out var accepted))
                            Fail(OmsPrivateRecoveryReadProblem.MissingAcceptedFact);
                        if (accepted!.Facts != facts || accepted.Kind != facts.Kind.ToString() ||
                            !CryptographicOperations.FixedTimeEquals(encoded.Sha256, accepted.Digest))
                            Fail(OmsPrivateRecoveryReadProblem.InvalidRecord);
                        witnessed.Add(key);
                    }
                    observations.Add(new(envelope, disposition));
                }
            }
            if (inbox.Keys.Any(x => !witnessed.Contains(x)))
                Fail(OmsPrivateRecoveryReadProblem.OrphanedAcceptedFact);
            await using (var command = Command($"SELECT EXISTS(SELECT 1 FROM oms_integrity_incidents WHERE {ScopeWhere})"))
                if (await command.ExecuteScalarAsync(cancellationToken) is true)
                    problems.Add(OmsPrivateRecoveryReadProblem.IntegrityIncident);
        }
        catch (ReadFailure failure)
        { problems.Add(failure.Problem); observations.Clear(); inbox.Clear(); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException or KeyNotFoundException)
        { problems.Add(OmsPrivateRecoveryReadProblem.InvalidRecord); observations.Clear(); inbox.Clear(); }
        await tx.CommitAsync(cancellationToken);
        return new(scope, snapshot, readAt, inbox.Count, observations.AsReadOnly(),
            problems.OrderBy(x => x).ToList().AsReadOnly());

        NpgsqlCommand Command(string sql)
        {
            var command = new NpgsqlCommand(sql, connection, tx);
            command.Parameters.AddWithValue("tenant", scope.Tenant.Value);
            command.Parameters.AddWithValue("venue", scope.Venue.Value);
            command.Parameters.AddWithValue("environment", (short)scope.Environment);
            command.Parameters.AddWithValue("account", scope.Account.Value);
            command.Parameters.AddWithValue("subaccount", scope.Subaccount?.Value ?? "");
            command.Parameters.AddWithValue("limit", maximumRows + 1);
            return command;
        }

        string Payload(NpgsqlDataReader rows, int column)
        {
            if (rows.IsDBNull(column)) Fail(OmsPrivateRecoveryReadProblem.CapacityExceeded);
            var raw = rows.GetString(column);
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(raw));
            if (bytes > MaximumBatchBytes) Fail(OmsPrivateRecoveryReadProblem.CapacityExceeded);
            return raw;
        }
    }

    private static OmsPrivateVenueFacts DecodeEvent(string raw, AccountKey scope)
    {
        using var doc = JsonDocument.Parse(raw);
        return DecodeEvent(doc.RootElement, scope);
    }

    private static OmsPrivateVenueFacts DecodeEvent(JsonElement root, AccountKey scope)
    {
        Fields(root, "schemaVersion", "tenant", "venue", "environment", "account", "subaccount", "facts");
        if (root.GetProperty("schemaVersion").GetInt32() != 2) Fail(OmsPrivateRecoveryReadProblem.UnsupportedSchema);
        if (root.GetProperty("tenant").GetString() != scope.Tenant.Value ||
            root.GetProperty("venue").GetString() != scope.Venue.Value ||
            root.GetProperty("environment").GetInt32() != (int)scope.Environment ||
            root.GetProperty("account").GetString() != scope.Account.Value ||
            root.GetProperty("subaccount").GetString() != (scope.Subaccount?.Value ?? ""))
            Fail(OmsPrivateRecoveryReadProblem.InvalidRecord);
        var raw = root.GetProperty("facts");
        var facts = raw.Deserialize<OmsPrivateVenueFacts>(Json)
            ?? throw new ReadFailure(OmsPrivateRecoveryReadProblem.InvalidRecord);
        facts.Validate();
        Fields(raw, JsonSerializer.SerializeToElement(facts, Json).EnumerateObject().Select(x => x.Name).ToArray());
        return facts;
    }

    private static void Fields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != fields.Length ||
            value.EnumerateObject().Any(x => !fields.Contains(x.Name, StringComparer.Ordinal)))
            Fail(OmsPrivateRecoveryReadProblem.InvalidRecord);
    }

    [DoesNotReturn]
    private static void Fail(OmsPrivateRecoveryReadProblem problem) => throw new ReadFailure(problem);
    private sealed class ReadFailure(OmsPrivateRecoveryReadProblem problem) : Exception
    { public OmsPrivateRecoveryReadProblem Problem { get; } = problem; }
    private sealed record InboxRow(OmsPrivateVenueFacts Facts, string Kind, byte[] Digest);
}
