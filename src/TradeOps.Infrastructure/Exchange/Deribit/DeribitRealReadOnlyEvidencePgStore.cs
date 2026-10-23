using System.Data;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace TradeOps.Infrastructure.Exchange.Deribit;

/// <summary>
/// Opt-in, non-DI real read-only evidence writer. No schema creation and no
/// exchange mutations. The owner must separately approve and migrate the
/// exact persistent PostgreSQL target and provision the account binding.
/// </summary>
public sealed class DeribitRealReadOnlyEvidencePgStore
{
    private readonly string _connectionString;
    private readonly string _expectedDatabase;
    private readonly string _expectedUser;

    public DeribitRealReadOnlyEvidencePgStore(
        string connectionString, string approvedHost, int approvedPort,
        string approvedDatabase, string approvedUser)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(approvedHost)
            || approvedHost.Contains(',') || approvedHost.Contains(';')
            || approvedPort is < 1 or > 65535
            || string.IsNullOrWhiteSpace(approvedDatabase)
            || string.IsNullOrWhiteSpace(approvedUser)
            || !string.Equals(b.Host, approvedHost, StringComparison.Ordinal)
            || b.Port != approvedPort
            || !string.Equals(b.Database, approvedDatabase, StringComparison.Ordinal)
            || !string.Equals(b.Username, approvedUser, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(b.SearchPath)
            || !string.IsNullOrEmpty(b.Options)
            || (approvedHost is not ("127.0.0.1" or "localhost" or "::1")
                && b.SslMode != SslMode.VerifyFull))
        {
            throw new InvalidOperationException(
                "The PostgreSQL connection does not match the independently approved target.");
        }

        _connectionString = b.ConnectionString;
        _expectedDatabase = approvedDatabase;
        _expectedUser = approvedUser;
    }

    /// <summary>
    /// Not an exchange account ID: fingerprint of the API credential ID
    /// used only for an owner-approved local binding. A rotated credential
    /// must not be implicitly treated as the same exchange account.
    /// </summary>
    public static string FingerprintCredentialId(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)
            || clientId != clientId.Trim()
            || clientId.Length > 256
            || clientId.Any(char.IsControl))
        {
            throw new InvalidOperationException("Invalid local credential identity.");
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(clientId))).ToLowerInvariant();
    }

    public static void Validate(DeribitPositionSnapshotCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.SnapshotId == Guid.Empty
            || capture.BindingId == Guid.Empty
            || capture.CredentialIdSha256 is null
            || capture.CredentialIdSha256.Length != 64
            || !capture.CredentialIdSha256.All(
                c => c is >= 'a' and <= 'f' or >= '0' and <= '9')
            || capture.RequestStartedAt.Offset != TimeSpan.Zero
            || capture.ResponseReceivedAt.Offset != TimeSpan.Zero
            || capture.ResponseReceivedAt < capture.RequestStartedAt
            || capture.Positions is null || capture.Positions.Count > 10000)
        {
            throw new InvalidOperationException("Invalid real position capture metadata.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in capture.Positions)
        {
            if (item is null
                || !Safe(item.InstrumentName, 160)
                || !names.Add(item.InstrumentName)
                || item.Kind is not ("future" or "option" or "future_combo" or "option_combo")
                || item.Direction is not ("buy" or "sell" or "zero")
                || (item.Direction == "zero" && item.NativeSize != 0m)
                || (item.Kind == "option" && item.BaseCurrencySize is not null))
            {
                throw new InvalidOperationException("Invalid real position observation.");
            }
        }
    }

    private static bool Safe(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= max && value == value.Trim()
        && !value.Any(char.IsControl);

    /// <summary>
    /// One PostgreSQL transaction per immutable snapshot. Does not create
    /// bindings, schema or history watermarks. A repeated snapshot_id fails
    /// closed instead of overwriting older evidence.
    /// </summary>
    public async Task<int> WritePositionSnapshotAsync(
        DeribitPositionSnapshotCapture capture,
        CancellationToken cancellationToken = default)
    {
        Validate(capture);
        var positions = capture.Positions.ToArray();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        await using (var identity = new NpgsqlCommand(
            "SELECT current_database(), current_user", connection, transaction))
        await using (var reader = await identity.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || reader.GetString(0) != _expectedDatabase
                || reader.GetString(1) != _expectedUser)
            {
                throw new InvalidOperationException("PostgreSQL session identity mismatch.");
            }
        }

        // SELECT-only binding check: PostgreSQL FOR SHARE additionally needs
        // UPDATE privileges, which this importer must NEVER receive.
        // Bindings are provisioned/changed only by separate owner administration.
        await using (var binding = new NpgsqlCommand(
            """
            SELECT 1 FROM broker_evidence.account_bindings
            WHERE binding_id = @binding AND provider = 'Deribit'
                AND environment = 'mainnet'
                AND provenance = 'OwnerApprovedLocalCredentialBinding'
                AND credential_id_sha256 = @fingerprint
            """, connection, transaction))
        {
            binding.Parameters.AddWithValue("binding", capture.BindingId);
            binding.Parameters.AddWithValue("fingerprint", capture.CredentialIdSha256);
            if (await binding.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException(
                    "No independently owner-approved Deribit account binding.");
            }
        }

        await using (var snapshot = new NpgsqlCommand(
            """
            INSERT INTO broker_evidence.position_snapshots
            (snapshot_id, binding_id, request_started_at,
             response_received_at, positions_reported, source_endpoint)
            VALUES (@id, @binding, @started, @received, @count,
                    'private/get_positions?currency=any')
            """, connection, transaction))
        {
            snapshot.Parameters.AddWithValue("id", capture.SnapshotId);
            snapshot.Parameters.AddWithValue("binding", capture.BindingId);
            snapshot.Parameters.AddWithValue("started", capture.RequestStartedAt);
            snapshot.Parameters.AddWithValue("received", capture.ResponseReceivedAt);
            snapshot.Parameters.AddWithValue("count", positions.Length);
            if (await snapshot.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Position snapshot not inserted.");
        }

        foreach (var item in positions)
        {
            await using var observation = new NpgsqlCommand(
                """
                INSERT INTO broker_evidence.position_observations
                (snapshot_id, instrument_name, kind, direction, native_size, size_currency)
                VALUES (@snapshot, @instrument, @kind, @direction, @size, @sizeCurrency)
                """, connection, transaction);
            observation.Parameters.AddWithValue("snapshot", capture.SnapshotId);
            observation.Parameters.AddWithValue("instrument", item.InstrumentName);
            observation.Parameters.AddWithValue("kind", item.Kind);
            observation.Parameters.AddWithValue("direction", item.Direction);
            observation.Parameters.AddWithValue("size", item.NativeSize);
            observation.Parameters.Add("sizeCurrency", NpgsqlTypes.NpgsqlDbType.Numeric).Value =
                item.BaseCurrencySize is decimal value ? value : DBNull.Value;
            if (await observation.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Position observation not inserted.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return positions.Length;
    }
}

public sealed record DeribitPositionSnapshotCapture(
    Guid SnapshotId,
    Guid BindingId,
    string CredentialIdSha256,
    DateTimeOffset RequestStartedAt,
    DateTimeOffset ResponseReceivedAt,
    IReadOnlyList<DeribitPositionEvidence> Positions);
