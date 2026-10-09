using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TradeOps.Infrastructure.Persistence;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class SignalTransitionMetricsIndexPostgresTests
{
    private static readonly DateTimeOffset FixtureStart =
        new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private const int SignalCount = 40_000;

    [Fact]
    public async Task Migration_ProvidesSelectiveOccurredAtAccessPathWithoutTransitionEventSeqScan()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable("TRADEOPS_TEST_POSTGRES_ADMIN");
        var isGitHubActions = string.Equals(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(adminConnectionString) && !isGitHubActions)
        {
            return;
        }

        adminConnectionString ??=
            "Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres";

        var databaseName = $"tradeops_signal_transition_index_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = "postgres",
            Pooling = false
        };
        var testBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
        {
            Database = databaseName,
            Pooling = false,
            CommandTimeout = 120
        };

        await using var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await adminConnection.OpenAsync();

        await using (var createDatabase = new NpgsqlCommand(
                         $"CREATE DATABASE \"{databaseName}\"",
                         adminConnection))
        {
            await createDatabase.ExecuteNonQueryAsync();
        }

        try
        {
            var options = new DbContextOptionsBuilder<TradeOpsDbContext>()
                .UseNpgsql(testBuilder.ConnectionString, npgsql => npgsql.CommandTimeout(120))
                .Options;

            await using var dbContext = new TradeOpsDbContext(options);
            await dbContext.Database.MigrateAsync();
            await SeedRepresentativeVolumeAsync(dbContext);

            await using var connection = new NpgsqlConnection(testBuilder.ConnectionString);
            await connection.OpenAsync();

            Assert.True(await IndexExistsAsync(connection));

            var from = FixtureStart.AddDays(14);
            var to = from.AddHours(1);

            var unscoped = await ExplainAsync(
                connection,
                UnscopedWindowSql,
                Timestamp("fromInclusive", from),
                Timestamp("toExclusive", to));

            Assert.False(unscoped.TransitionEventSequentialScan);
            Assert.Contains(
                "IX_TradingSignalOutcomeEvents_OccurredAt",
                unscoped.Indexes);

            var symbolScoped = await ExplainAsync(
                connection,
                SymbolScopedWindowSql,
                Timestamp("fromInclusive", from),
                Timestamp("toExclusive", to),
                Varchar("symbol", "BTCUSDT"));

            Assert.False(symbolScoped.TransitionEventSequentialScan);
        }
        finally
        {
            await using var dropDatabase = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
                adminConnection);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedRepresentativeVolumeAsync(TradeOpsDbContext dbContext)
    {
        dbContext.Database.SetCommandTimeout(TimeSpan.FromMinutes(2));

        await dbContext.Database.ExecuteSqlRawAsync($$"""
            INSERT INTO "TradingSignals" (
                "Id", "Symbol", "Side", "SignalType", "RequestedQuantity", "RiskPercent",
                "StopLoss", "TakeProfit", "CreatedAt", "Source", "Outcome",
                "RiskRejectionReasons", "OrderId", "ClientOrderId")
            SELECT
                md5('transition-index-signal-' || g::text)::uuid,
                CASE g % 8
                    WHEN 0 THEN 'BTCUSDT'
                    WHEN 1 THEN 'ETHUSDT'
                    WHEN 2 THEN 'SOLUSDT'
                    WHEN 3 THEN 'XRPUSDT'
                    WHEN 4 THEN 'ADAUSDT'
                    WHEN 5 THEN 'DOGEUSDT'
                    WHEN 6 THEN 'LINKUSDT'
                    ELSE 'AVAXUSDT'
                END,
                CASE WHEN g % 2 = 0 THEN 'Buy' ELSE 'Sell' END,
                'TransitionIndex',
                1.0,
                NULL,
                NULL,
                NULL,
                TIMESTAMPTZ '2026-09-01 00:00:00+00' + ((g - 1) * INTERVAL '60 seconds'),
                'TransitionIndexFixture',
                CASE WHEN g % 2 = 0 THEN 'Accepted' ELSE 'Rejected' END,
                ARRAY[]::text[],
                NULL,
                NULL
            FROM generate_series(1, {{SignalCount}}) AS g;

            INSERT INTO "TradingSignalOutcomeEvents" (
                "Id", "TradingSignalId", "PreviousOutcome", "Outcome", "OccurredAt",
                "RiskRejectionReasons", "OrderId", "ClientOrderId")
            SELECT
                md5('transition-index-received-' || g::text)::uuid,
                md5('transition-index-signal-' || g::text)::uuid,
                NULL,
                'Received',
                TIMESTAMPTZ '2026-09-01 00:00:00+00' + ((g - 1) * INTERVAL '60 seconds'),
                ARRAY[]::text[],
                NULL,
                NULL
            FROM generate_series(1, {{SignalCount}}) AS g;

            INSERT INTO "TradingSignalOutcomeEvents" (
                "Id", "TradingSignalId", "PreviousOutcome", "Outcome", "OccurredAt",
                "RiskRejectionReasons", "OrderId", "ClientOrderId")
            SELECT
                md5('transition-index-terminal-' || g::text)::uuid,
                md5('transition-index-signal-' || g::text)::uuid,
                'Received',
                CASE WHEN g % 2 = 0 THEN 'Accepted' ELSE 'Rejected' END,
                TIMESTAMPTZ '2026-09-01 00:00:30+00' + ((g - 1) * INTERVAL '60 seconds'),
                CASE
                    WHEN g % 2 = 0 THEN ARRAY[]::text[]
                    ELSE ARRAY['Representative rejection']::text[]
                END,
                NULL,
                NULL
            FROM generate_series(1, {{SignalCount}}) AS g;

            ANALYZE "TradingSignals";
            ANALYZE "TradingSignalOutcomeEvents";
            """);
    }

    private static async Task<bool> IndexExistsAsync(NpgsqlConnection connection)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_indexes
                WHERE schemaname = 'public'
                  AND tablename = 'TradingSignalOutcomeEvents'
                  AND indexname = 'IX_TradingSignalOutcomeEvents_OccurredAt')
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        return (bool)(await command.ExecuteScalarAsync() ?? false);
    }

    private static async Task<PlanFacts> ExplainAsync(
        NpgsqlConnection connection,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(
            $"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) {sql}",
            connection)
        {
            CommandTimeout = 120
        };

        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        var json = (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("EXPLAIN returned no plan."));
        using var document = JsonDocument.Parse(json);

        var indexes = new SortedSet<string>(StringComparer.Ordinal);
        var transitionEventSequentialScan = false;
        CollectPlanFacts(document.RootElement[0].GetProperty("Plan"), indexes, ref transitionEventSequentialScan);

        return new PlanFacts(indexes.ToArray(), transitionEventSequentialScan);
    }

    private static void CollectPlanFacts(
        JsonElement node,
        ISet<string> indexes,
        ref bool transitionEventSequentialScan)
    {
        var nodeType = node.TryGetProperty("Node Type", out var nodeTypeElement)
            ? nodeTypeElement.GetString()
            : null;
        var relation = node.TryGetProperty("Relation Name", out var relationElement)
            ? relationElement.GetString()
            : null;

        if (node.TryGetProperty("Index Name", out var indexElement) && indexElement.GetString() is { } indexName)
        {
            indexes.Add(indexName);
        }

        if (string.Equals(nodeType, "Seq Scan", StringComparison.Ordinal) &&
            string.Equals(relation, "TradingSignalOutcomeEvents", StringComparison.Ordinal))
        {
            transitionEventSequentialScan = true;
        }

        if (!node.TryGetProperty("Plans", out var plansElement))
        {
            return;
        }

        foreach (var child in plansElement.EnumerateArray())
        {
            CollectPlanFacts(child, indexes, ref transitionEventSequentialScan);
        }
    }

    private static NpgsqlParameter Timestamp(string name, DateTimeOffset value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = value };

    private static NpgsqlParameter Varchar(string name, string value) =>
        new(name, NpgsqlDbType.Varchar) { Value = value };

    private sealed record PlanFacts(
        IReadOnlyList<string> Indexes,
        bool TransitionEventSequentialScan);

    private const string UnscopedWindowSql = """
        SELECT transition."Outcome", COUNT(*)
        FROM "TradingSignalOutcomeEvents" AS transition
        WHERE transition."OccurredAt" >= @fromInclusive
          AND transition."OccurredAt" < @toExclusive
        GROUP BY transition."Outcome"
        """;

    private const string SymbolScopedWindowSql = """
        SELECT transition."Outcome", COUNT(*)
        FROM "TradingSignalOutcomeEvents" AS transition
        JOIN "TradingSignals" AS signal
          ON signal."Id" = transition."TradingSignalId"
        WHERE transition."OccurredAt" >= @fromInclusive
          AND transition."OccurredAt" < @toExclusive
          AND signal."Symbol" = @symbol
        GROUP BY transition."Outcome"
        """;
}
