using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Infrastructure.Persistence.Oms;

/// <summary>
/// Local qualification admission arbitration. Must accompany the owner launcher's exclusive
/// local file lock. Session loss refuses subsequent sends; this is NOT general B5 fencing.
/// Persistent original Place commands consume the three-slot stage budget, including unsent
/// and uncertain attempts. There is no filesystem counter reset or automatic resend.
/// </summary>
public sealed class OmsPostgresBoundedQualificationLease : IAsyncDisposable
{
    private readonly NpgsqlConnection connection;
    private readonly NpgsqlDataSource journalSource;
    private readonly IOmsRecoveryLedgerReader reader;
    private readonly AccountKey scope;
    private readonly int backend;
    private readonly long key;
    private bool reserved;
    private OmsPostgresBoundedQualificationLease(NpgsqlConnection connection, NpgsqlDataSource source,
        IOmsRecoveryLedgerReader reader, AccountKey scope, long key)
    { this.connection = connection; journalSource = source; this.reader = reader; this.scope = scope; this.key = key; backend = connection.ProcessID; }

    public static async Task<OmsPostgresBoundedQualificationLease> AcquireAsync(NpgsqlDataSource source,
        IOmsRecoveryLedgerReader reader, AccountKey scope, CancellationToken ct = default)
    {
        if (scope.Venue.Value != "DERIBIT" || scope.Environment != OmsEnvironment.Mainnet)
            throw new ArgumentException("Pinned mainnet scope required.");
        var connection = await source.OpenConnectionAsync(ct);
        try
        {
            var identity = JsonSerializer.SerializeToUtf8Bytes(new[] { "bounded-mainnet-oms-admission:v1", scope.Tenant.Value,
                scope.Venue.Value, ((int)scope.Environment).ToString(System.Globalization.CultureInfo.InvariantCulture),
                scope.Account.Value, scope.Subaccount?.Value ?? "" });
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(identity));
            command.Parameters.AddWithValue("key", key);
            if (await command.ExecuteScalarAsync(ct) is not true) throw new InvalidOperationException("Qualification scope busy.");
            return new(connection, source, reader, scope, key);
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (connection.State != ConnectionState.Open) throw new InvalidOperationException("Qualification lease lost.");
        await using var check = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
        if (await check.ExecuteScalarAsync(ct) is not int actual || actual != backend)
            throw new InvalidOperationException("Qualification DB session changed.");
    }

    public async Task<int> ReserveAsync(string stage, OmsJournalCommandRequest request, CancellationToken ct = default)
    {
        if (reserved || stage != DeribitBoundedQualificationPolicy.CurrentStage || request.Scope != scope || request.Payload.Kind != OmsJournalCommandKind.Place ||
            request.ExpectedAggregateVersion.Value != 0 || request.Payload.Provenance != DeribitBoundedQualificationPolicy.Provenance(stage) ||
            request.Payload.OperatorOrStrategyId != DeribitBoundedQualificationPolicy.Operator ||
            request.Payload.Instrument != "ETH-PERPETUAL" || request.Payload.Quantity != 1 ||
            request.Payload.QuantityUnit != OmsQuantityUnit.QuoteAsset || !request.Payload.PostOnly || request.Payload.ReduceOnly ||
            request.Payload.Side != TradeOps.Domain.Enums.OrderSide.Buy || request.Payload.OrderType != TradeOps.Domain.Enums.OrderType.Limit ||
            request.Payload.TimeInForce is not ("GTC" or "good_til_cancelled") || request.Payload.TriggerPrice.HasValue)
            throw new ArgumentException("Invalid qualification reservation.");
        await CheckAsync(ct);
        var started = DateTimeOffset.UtcNow;
        var snapshot = await reader.ReadAsync(scope, cancellationToken: ct);
        if (snapshot.Private.ReadAtUtc.Offset != TimeSpan.Zero || snapshot.Private.ReadAtUtc < started.AddSeconds(-5) ||
            snapshot.Private.ReadAtUtc > DateTimeOffset.UtcNow.AddSeconds(5)) throw new InvalidOperationException("Stale admission snapshot.");
        var spent = DeribitBoundedQualificationPolicy.ValidateAdmission(snapshot, scope, stage);
        if (await new OmsPostgresJournal(journalSource).PersistAsync(request, cancellationToken: ct) != OmsJournalWriteResult.Created)
            throw new InvalidOperationException("New qualification command was not created.");
        reserved = true;
        return spent + 1;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (connection.State == ConnectionState.Open)
            {
                await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                release.Parameters.AddWithValue("key", key);
                await release.ExecuteScalarAsync();
            }
        }
        finally { await connection.DisposeAsync(); }
    }
}
