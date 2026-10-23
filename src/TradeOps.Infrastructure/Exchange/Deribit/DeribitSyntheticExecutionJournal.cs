using Npgsql;
using NpgsqlTypes;

namespace TradeOps.Infrastructure.Exchange.Deribit;

/// <summary>
/// Synthetic-only standalone PostgreSQL evidence journal. Never writes
/// TradeOps Orders/Fills or authenticates to a broker. Do NOT DI-register.
/// Restricted to a disposable loopback test DB at construction time.
/// </summary>
public sealed class DeribitSyntheticExecutionJournal
{
    private readonly string _connectionString;
    public const string Provider = "DeribitSynthetic";
    public const string Table = "SyntheticDeribitExecutionEvidence";

    public DeribitSyntheticExecutionJournal(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        if (b.Host != "127.0.0.1"
            || string.IsNullOrWhiteSpace(b.Database)
            || !b.Database.StartsWith("tradeops_api_smoke_", StringComparison.Ordinal)
            || b.Database.Length > 63
            || !b.Database.All(c => c is >= 'a' and <= 'z'
                or >= '0' and <= '9' or '_')
            || !string.IsNullOrWhiteSpace(b.SearchPath)
            || !string.IsNullOrWhiteSpace(b.Options))
        {
            throw new InvalidOperationException(
                "Synthetic evidence journal requires a disposable loopback PostgreSQL database.");
        }
        _connectionString = b.ConnectionString;
    }

    public static void Validate(DeribitSyntheticExecution item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Safe(item.AccountKey, 128)
            || !item.AccountKey.StartsWith("synthetic-account-", StringComparison.Ordinal)
            || !Safe(item.ExecutionId, 160)
            || !Safe(item.BrokerOrderId, 160)
            || !Safe(item.InstrumentName, 160)
            || item.Direction is not ("buy" or "sell")
            || item.NativeAmount <= 0m || item.Price <= 0m
            || item.TimestampMs <= 0
            || !Safe(item.FeeCurrency, 16)
            || item.Contracts is < 0m)
        {
            throw new InvalidOperationException(
                "Invalid synthetic-only execution evidence; no database write allowed.");
        }
    }

    private static bool Safe(string? text, int max) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Length <= max
        && text == text.Trim()
        && !text.Any(char.IsControl);

    public async Task InitializeDisposableAsync(
        CancellationToken cancellationToken = default)
    {
        await using var cn = new NpgsqlConnection(_connectionString);
        await cn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            """
            CREATE TABLE IF NOT EXISTS "SyntheticDeribitExecutionEvidence" (
              "ProviderKey" varchar(32) NOT NULL DEFAULT 'DeribitSynthetic',
              "AccountKey" varchar(128) NOT NULL,
              "ExecutionId" varchar(160) NOT NULL,
              "BrokerOrderId" varchar(160) NOT NULL,
              "InstrumentName" varchar(160) NOT NULL,
              "Direction" varchar(4) NOT NULL,
              "NativeAmount" numeric NOT NULL,
              "Price" numeric NOT NULL,
              "Fee" numeric NOT NULL,
              "FeeCurrency" varchar(16) NOT NULL,
              "TimestampMs" bigint NOT NULL,
              "Contracts" numeric NULL,
              "InsertedAt" timestamptz NOT NULL DEFAULT now(),
              CONSTRAINT "PK_SyntheticDeribitEvidence" PRIMARY KEY
                ("ProviderKey","AccountKey","ExecutionId"),
              CONSTRAINT "CK_SyntheticDeribitProvider"
                CHECK ("ProviderKey" = 'DeribitSynthetic'),
              CONSTRAINT "CK_SyntheticDeribitAccount"
                CHECK ("AccountKey" LIKE 'synthetic-account-%'),
              CONSTRAINT "CK_SyntheticDeribitTerms"
                CHECK ("NativeAmount" > 0 AND "Price" > 0
                   AND "TimestampMs" > 0
                   AND "Direction" IN ('buy','sell')
                   AND ("Contracts" IS NULL OR "Contracts" >= 0))
            );
            """, cn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DeribitSyntheticWriteDisposition> WriteAsync(
        DeribitSyntheticExecution item,
        CancellationToken cancellationToken = default)
    {
        Validate(item);
        await using var cn = new NpgsqlConnection(_connectionString);
        await cn.OpenAsync(cancellationToken);
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO "SyntheticDeribitExecutionEvidence"
            ("ProviderKey","AccountKey","ExecutionId","BrokerOrderId",
             "InstrumentName","Direction","NativeAmount","Price","Fee",
             "FeeCurrency","TimestampMs","Contracts")
            VALUES ('DeribitSynthetic',@account,@execution,@broker,
             @instrument,@side,@amount,@price,@fee,
             @feeCurrency,@time,@contracts)
            ON CONFLICT DO NOTHING
            """, cn))
        {
            AddParameters(insert, item);
            var inserted = await insert.ExecuteNonQueryAsync(cancellationToken);
            if (inserted == 1)
                return DeribitSyntheticWriteDisposition.Inserted;
            if (inserted != 0)
                throw new InvalidOperationException("Unexpected synthetic insertion count.");
        }

        await using var query = new NpgsqlCommand(
            """
            SELECT "BrokerOrderId","InstrumentName","Direction",
                   "NativeAmount","Price","Fee","FeeCurrency",
                   "TimestampMs","Contracts"
            FROM "SyntheticDeribitExecutionEvidence"
            WHERE "ProviderKey"='DeribitSynthetic'
                AND "AccountKey"=@account
                AND "ExecutionId"=@execution
            """, cn);
        query.Parameters.AddWithValue("account", item.AccountKey);
        query.Parameters.AddWithValue("execution", item.ExecutionId);
        await using var rdr = await query.ExecuteReaderAsync(cancellationToken);
        if (!await rdr.ReadAsync(cancellationToken))
            return DeribitSyntheticWriteDisposition.IdentityConflict;

        var equal = string.Equals(rdr.GetString(0), item.BrokerOrderId, StringComparison.Ordinal)
            && string.Equals(rdr.GetString(1), item.InstrumentName, StringComparison.Ordinal)
            && string.Equals(rdr.GetString(2), item.Direction, StringComparison.Ordinal)
            && rdr.GetDecimal(3) == item.NativeAmount
            && rdr.GetDecimal(4) == item.Price
            && rdr.GetDecimal(5) == item.Fee
            && string.Equals(rdr.GetString(6), item.FeeCurrency, StringComparison.Ordinal)
            && rdr.GetInt64(7) == item.TimestampMs
            && (rdr.IsDBNull(8) ? (decimal?)null : rdr.GetDecimal(8)) == item.Contracts;

        return equal
            ? DeribitSyntheticWriteDisposition.EquivalentReplay
            : DeribitSyntheticWriteDisposition.IdentityConflict;
    }

    private static void AddParameters(
        NpgsqlCommand cmd, DeribitSyntheticExecution item)
    {
        cmd.Parameters.AddWithValue("account", item.AccountKey);
        cmd.Parameters.AddWithValue("execution", item.ExecutionId);
        cmd.Parameters.AddWithValue("broker", item.BrokerOrderId);
        cmd.Parameters.AddWithValue("instrument", item.InstrumentName);
        cmd.Parameters.AddWithValue("side", item.Direction);
        cmd.Parameters.AddWithValue("amount", item.NativeAmount);
        cmd.Parameters.AddWithValue("price", item.Price);
        cmd.Parameters.AddWithValue("fee", item.Fee);
        cmd.Parameters.AddWithValue("feeCurrency", item.FeeCurrency);
        cmd.Parameters.AddWithValue("time", item.TimestampMs);
        cmd.Parameters.Add("contracts", NpgsqlDbType.Numeric).Value =
            item.Contracts is decimal value ? value : DBNull.Value;
    }
}

public sealed record DeribitSyntheticExecution(
    string AccountKey,
    string ExecutionId,
    string BrokerOrderId,
    string InstrumentName,
    string Direction,
    decimal NativeAmount,
    decimal Price,
    decimal Fee,
    string FeeCurrency,
    long TimestampMs,
    decimal? Contracts);

public enum DeribitSyntheticWriteDisposition
{
    Inserted,
    EquivalentReplay,
    IdentityConflict
}
