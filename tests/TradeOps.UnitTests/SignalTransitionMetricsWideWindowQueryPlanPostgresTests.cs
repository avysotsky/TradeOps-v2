using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace TradeOps.UnitTests;

public sealed class SignalTransitionMetricsWideWindowQueryPlanPostgresTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset FixtureStart =
        new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan BucketSize = TimeSpan.FromHours(1);

    private const int SignalCount = 40_000;
    private const int EventCount = SignalCount * 2;
    private const int BySymbolLimit = 20;

    [Fact]
    public async Task RepresentativeVolume_CharacterizesTwentyFourHourSeriesAndBySymbolPlans()
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

        var databaseName = $"tradeops_signal_transition_wide_plan_{Guid.NewGuid():N}";
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

            var windowFrom = FixtureStart.AddDays(14);
            var windowTo = windowFrom.AddHours(24);
            const string symbol = "BTCUSDT";

            var repository = new EfSignalTransitionMetricsRepository(dbContext);

            var allSeries = await repository.GetSeriesAsync(
                windowFrom,
                windowTo,
                "1h",
                BucketSize);
            var btcSeries = await repository.GetSeriesAsync(
                windowFrom,
                windowTo,
                "1h",
                BucketSize,
                symbol);
            var bySymbol = await repository.GetBySymbolAsync(
                windowFrom,
                windowTo,
                BySymbolLimit);

            Assert.Equal(24, allSeries.Buckets.Count);
            Assert.Equal(24, btcSeries.Buckets.Count);
            Assert.Equal(1_440, allSeries.Buckets.Sum(item => item.ReceivedTransitions));
            Assert.Equal(720, allSeries.Buckets.Sum(item => item.AcceptedTransitions));
            Assert.Equal(720, allSeries.Buckets.Sum(item => item.RejectedTransitions));
            Assert.Equal(180, btcSeries.Buckets.Sum(item => item.ReceivedTransitions));
            Assert.Equal(180, btcSeries.Buckets.Sum(item => item.AcceptedTransitions));
            Assert.Equal(0, btcSeries.Buckets.Sum(item => item.RejectedTransitions));
            Assert.False(bySymbol.IsTruncated);
            Assert.Equal(8, bySymbol.Symbols.Count);
            Assert.Equal(2_880, bySymbol.Symbols.Sum(item => item.TotalTransitions));

            await using var connection = new NpgsqlConnection(testBuilder.ConnectionString);
            await connection.OpenAsync();

            var plans = new[]
            {
                await ExplainAsync(
                    connection,
                    "transition-series-unscoped-24h",
                    UnscopedSeriesSql,
                    Timestamp("fromInclusive", windowFrom),
                    Timestamp("toExclusive", windowTo),
                    Interval("bucketSize", BucketSize)),
                await ExplainAsync(
                    connection,
                    "transition-series-symbol-scoped-24h",
                    SymbolScopedSeriesSql,
                    Timestamp("fromInclusive", windowFrom),
                    Timestamp("toExclusive", windowTo),
                    Interval("bucketSize", BucketSize),
                    Varchar("symbol", symbol)),
                await ExplainAsync(
                    connection,
                    "transition-by-symbol-24h",
                    BySymbolSql,
                    Timestamp("fromInclusive", windowFrom),
                    Timestamp("toExclusive", windowTo),
                    Integer("fetchLimit", BySymbolLimit + 1))
            };

            Assert.All(plans, plan =>
            {
                Assert.True(plan.TouchesTransitionEvents, $"{plan.Name} did not touch TradingSignalOutcomeEvents.");
                Assert.True(plan.ExecutionMs >= 0, $"{plan.Name} has an invalid execution time.");
            });

            var serverVersion = await GetServerVersionAsync(connection);
            var report = BuildReport(serverVersion, plans);
            output.WriteLine(report);
            await PublishReportAsync(report);
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
                md5('transition-wide-plan-signal-' || g::text)::uuid,
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
                'TransitionWidePlan',
                1.0,
                NULL,
                NULL,
                NULL,
                TIMESTAMPTZ '2026-09-01 00:00:00+00' + ((g - 1) * INTERVAL '60 seconds'),
                'TransitionWidePlanFixture',
                CASE WHEN g % 2 = 0 THEN 'Accepted' ELSE 'Rejected' END,
                ARRAY[]::text[],
                NULL,
                NULL
            FROM generate_series(1, {{SignalCount}}) AS g;

            INSERT INTO "TradingSignalOutcomeEvents" (
                "Id", "TradingSignalId", "PreviousOutcome", "Outcome", "OccurredAt",
                "RiskRejectionReasons", "OrderId", "ClientOrderId")
            SELECT
                md5('transition-wide-plan-received-' || g::text)::uuid,
                md5('transition-wide-plan-signal-' || g::text)::uuid,
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
                md5('transition-wide-plan-terminal-' || g::text)::uuid,
                md5('transition-wide-plan-signal-' || g::text)::uuid,
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

    private static async Task<PlanSummary> ExplainAsync(
        NpgsqlConnection connection,
        string name,
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

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var json = reader.GetString(0);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement[0];
        var plan = root.GetProperty("Plan");
        var indexes = new SortedSet<string>(StringComparer.Ordinal);
        var touchesTransitionEvents = false;
        var transitionSequentialScan = false;
        CollectPlanFacts(plan, indexes, ref touchesTransitionEvents, ref transitionSequentialScan);

        return new PlanSummary(
            name,
            root.GetProperty("Planning Time").GetDouble(),
            root.GetProperty("Execution Time").GetDouble(),
            plan.TryGetProperty("Actual Rows", out var actualRows) ? actualRows.GetDouble() : 0,
            plan.TryGetProperty("Shared Hit Blocks", out var sharedHitBlocks) ? sharedHitBlocks.GetInt64() : 0,
            plan.TryGetProperty("Shared Read Blocks", out var sharedReadBlocks) ? sharedReadBlocks.GetInt64() : 0,
            indexes.ToArray(),
            touchesTransitionEvents,
            transitionSequentialScan);
    }

    private static void CollectPlanFacts(
        JsonElement node,
        ISet<string> indexes,
        ref bool touchesTransitionEvents,
        ref bool transitionSequentialScan)
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

        if (string.Equals(relation, "TradingSignalOutcomeEvents", StringComparison.Ordinal))
        {
            touchesTransitionEvents = true;
            if (string.Equals(nodeType, "Seq Scan", StringComparison.Ordinal))
            {
                transitionSequentialScan = true;
            }
        }

        if (!node.TryGetProperty("Plans", out var plansElement))
        {
            return;
        }

        foreach (var child in plansElement.EnumerateArray())
        {
            CollectPlanFacts(child, indexes, ref touchesTransitionEvents, ref transitionSequentialScan);
        }
    }

    private static async Task<string> GetServerVersionAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SHOW server_version;", connection);
        return (string)(await command.ExecuteScalarAsync() ?? "unknown");
    }

    private static string BuildReport(string serverVersion, IReadOnlyList<PlanSummary> plans)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## TradeOps signal-transition 24h query-plan characterization");
        builder.AppendLine();
        builder.AppendLine($"- PostgreSQL: `{serverVersion}`");
        builder.AppendLine($"- Fixture: `{SignalCount:N0}` signals, `{EventCount:N0}` persisted outcome events");
        builder.AppendLine("- Distribution: deterministic ~28-day time range, 8 symbols, two outcome events per signal");
        builder.AppendLine("- Characterization window: 24 hours; series bucket: 1 hour; by-symbol limit: 20");
        builder.AppendLine("- This is characterization, not a production SLO or a no-Seq-Scan invariant.");
        builder.AppendLine("- PostgreSQL may legitimately choose a sequential scan when a wider window reduces selectivity.");
        builder.AppendLine();
        builder.AppendLine("| Plan | Planning ms | Execution ms | Root rows | Shared hit | Shared read | Indexes | Transition-event seq scan |");
        builder.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | --- | --- |");

        foreach (var plan in plans)
        {
            var indexes = plan.Indexes.Count == 0 ? "none" : string.Join(", ", plan.Indexes);
            builder.AppendLine(
                $"| {plan.Name} | {plan.PlanningMs:F2} | {plan.ExecutionMs:F2} | {plan.RootActualRows:F0} | {plan.SharedHitBlocks} | {plan.SharedReadBlocks} | {indexes} | {(plan.TransitionSequentialScan ? "yes" : "no")} |");
        }

        return builder.ToString();
    }

    private static async Task PublishReportAsync(string report)
    {
        var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrWhiteSpace(summaryPath))
        {
            await File.AppendAllTextAsync(summaryPath, Environment.NewLine + report + Environment.NewLine);
        }

        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            await File.WriteAllTextAsync(
                Path.Combine(workspace, "signal-transition-wide-window-query-plan-report.md"),
                report);
        }
    }

    private static NpgsqlParameter Timestamp(string name, DateTimeOffset value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = value };

    private static NpgsqlParameter Interval(string name, TimeSpan value) =>
        new(name, NpgsqlDbType.Interval) { Value = value };

    private static NpgsqlParameter Varchar(string name, string value) =>
        new(name, NpgsqlDbType.Varchar) { Value = value };

    private static NpgsqlParameter Integer(string name, int value) =>
        new(name, NpgsqlDbType.Integer) { Value = value };

    private sealed record PlanSummary(
        string Name,
        double PlanningMs,
        double ExecutionMs,
        double RootActualRows,
        long SharedHitBlocks,
        long SharedReadBlocks,
        IReadOnlyList<string> Indexes,
        bool TouchesTransitionEvents,
        bool TransitionSequentialScan);

    private const string UnscopedSeriesSql = """
        WITH buckets AS (
            SELECT bucket_start
            FROM generate_series(
                date_bin(@bucketSize, @fromInclusive, TIMESTAMPTZ '1970-01-01 00:00:00+00'),
                @toExclusive,
                @bucketSize) AS bucket_start
            WHERE bucket_start < @toExclusive
        ),
        transition_counts AS (
            SELECT
                date_bin(@bucketSize, transition."OccurredAt", TIMESTAMPTZ '1970-01-01 00:00:00+00') AS bucket_start,
                COUNT(*) FILTER (WHERE transition."Outcome" = 'Received') AS received,
                COUNT(*) FILTER (WHERE transition."Outcome" = 'Accepted') AS accepted,
                COUNT(*) FILTER (WHERE transition."Outcome" = 'Rejected') AS rejected
            FROM "TradingSignalOutcomeEvents" AS transition
            WHERE transition."OccurredAt" >= @fromInclusive
              AND transition."OccurredAt" < @toExclusive
            GROUP BY 1
        )
        SELECT
            GREATEST(bucket.bucket_start, @fromInclusive) AS from_inclusive,
            LEAST(bucket.bucket_start + @bucketSize, @toExclusive) AS to_exclusive,
            COALESCE(transition.received, 0) AS received,
            COALESCE(transition.accepted, 0) AS accepted,
            COALESCE(transition.rejected, 0) AS rejected
        FROM buckets AS bucket
        LEFT JOIN transition_counts AS transition
            ON transition.bucket_start = bucket.bucket_start
        ORDER BY bucket.bucket_start;
        """;

    private const string SymbolScopedSeriesSql = """
        WITH buckets AS (
            SELECT bucket_start
            FROM generate_series(
                date_bin(@bucketSize, @fromInclusive, TIMESTAMPTZ '1970-01-01 00:00:00+00'),
                @toExclusive,
                @bucketSize) AS bucket_start
            WHERE bucket_start < @toExclusive
        ),
        transition_counts AS (
            SELECT
                date_bin(@bucketSize, transition."OccurredAt", TIMESTAMPTZ '1970-01-01 00:00:00+00') AS bucket_start,
                COUNT(*) FILTER (WHERE transition."Outcome" = 'Received') AS received,
                COUNT(*) FILTER (WHERE transition."Outcome" = 'Accepted') AS accepted,
                COUNT(*) FILTER (WHERE transition."Outcome" = 'Rejected') AS rejected
            FROM "TradingSignalOutcomeEvents" AS transition
            JOIN "TradingSignals" AS signal
                ON signal."Id" = transition."TradingSignalId"
            WHERE transition."OccurredAt" >= @fromInclusive
              AND transition."OccurredAt" < @toExclusive
              AND signal."Symbol" = @symbol
            GROUP BY 1
        )
        SELECT
            GREATEST(bucket.bucket_start, @fromInclusive) AS from_inclusive,
            LEAST(bucket.bucket_start + @bucketSize, @toExclusive) AS to_exclusive,
            COALESCE(transition.received, 0) AS received,
            COALESCE(transition.accepted, 0) AS accepted,
            COALESCE(transition.rejected, 0) AS rejected
        FROM buckets AS bucket
        LEFT JOIN transition_counts AS transition
            ON transition.bucket_start = bucket.bucket_start
        ORDER BY bucket.bucket_start;
        """;

    private const string BySymbolSql = """
        SELECT
            signal."Symbol" AS symbol,
            COUNT(*) AS total_transitions,
            COUNT(*) FILTER (WHERE transition."Outcome" = 'Received') AS received,
            COUNT(*) FILTER (WHERE transition."Outcome" = 'Accepted') AS accepted,
            COUNT(*) FILTER (WHERE transition."Outcome" = 'Rejected') AS rejected
        FROM "TradingSignalOutcomeEvents" AS transition
        JOIN "TradingSignals" AS signal
            ON signal."Id" = transition."TradingSignalId"
        WHERE transition."OccurredAt" >= @fromInclusive
          AND transition."OccurredAt" < @toExclusive
        GROUP BY signal."Symbol"
        ORDER BY total_transitions DESC, symbol ASC
        LIMIT @fetchLimit;
        """;
}
