using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TradeOps.Application.Models;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace TradeOps.UnitTests;

public sealed class ExecutionMetricsQueryPlanPostgresTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset FixtureStart =
        new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private const int OrderCount = 20_000;
    private const int SignalCount = 40_000;
    private const int LifecycleCount = 60_000;
    private const int FillCount = 20_000;

    [Fact]
    public async Task RepresentativeVolume_UsesSelectiveFactAccessPathsAndRecordsBaseline()
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

        var databaseName = $"tradeops_metrics_plan_{Guid.NewGuid():N}";
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
            var windowTo = windowFrom.AddHours(1);
            var seriesTo = windowFrom.AddHours(6);
            var bySymbolTo = windowFrom.AddDays(1);
            const string symbol = "BTCUSDT";

            var metricsRepository = new EfExecutionMetricsRepository(dbContext, new RiskSettings());
            var bySymbolRepository = new EfExecutionMetricsBySymbolRepository(dbContext);

            await metricsRepository.GetWindowAsync(windowFrom, windowTo, symbol);
            await metricsRepository.GetSeriesAsync(
                windowFrom,
                seriesTo,
                "5m",
                TimeSpan.FromMinutes(5),
                symbol);
            await bySymbolRepository.GetAsync(windowFrom, bySymbolTo, 50);

            var windowMedianMs = await MeasureMedianAsync(async () =>
            {
                _ = await metricsRepository.GetWindowAsync(windowFrom, windowTo, symbol);
            });
            var seriesMedianMs = await MeasureMedianAsync(async () =>
            {
                _ = await metricsRepository.GetSeriesAsync(
                    windowFrom,
                    seriesTo,
                    "5m",
                    TimeSpan.FromMinutes(5),
                    symbol);
            });
            var bySymbolMedianMs = await MeasureMedianAsync(async () =>
            {
                _ = await bySymbolRepository.GetAsync(windowFrom, bySymbolTo, 50);
            });

            await using var connection = new NpgsqlConnection(testBuilder.ConnectionString);
            await connection.OpenAsync();

            var plans = new List<PlanSummary>
            {
                await ExplainAsync(
                    connection,
                    "window-signals-symbol-scoped",
                    WindowSignalsSql,
                    Timestamp("fromInclusive", windowFrom),
                    Timestamp("toExclusive", windowTo),
                    Varchar("symbol", symbol)),
                await ExplainAsync(
                    connection,
                    "window-lifecycle-symbol-scoped",
                    WindowLifecycleSql,
                    Timestamp("fromInclusive", windowFrom),
                    Timestamp("toExclusive", windowTo),
                    Varchar("symbol", symbol)),
                await ExplainAsync(
                    connection,
                    "window-orders-touched-symbol-scoped",
                    WindowOrdersTouchedSql,
                    Timestamp("fromInclusive", windowFrom),
                    Timestamp("toExclusive", windowTo),
                    Varchar("symbol", symbol)),
                await ExplainAsync(
                    connection,
                    "window-fills-symbol-scoped",
                    WindowFillsSql,
                    Timestamp("fromInclusive", windowFrom),
                    Timestamp("toExclusive", windowTo),
                    Varchar("symbol", symbol)),
                await ExplainAsync(
                    connection,
                    "series-5m-symbol-scoped",
                    SeriesSql,
                    Timestamp("fromInclusive", windowFrom),
                    Timestamp("toExclusive", seriesTo),
                    Interval("bucketSize", TimeSpan.FromMinutes(5)),
                    Varchar("symbol", symbol)),
                await ExplainAsync(
                    connection,
                    "by-symbol-24h",
                    BySymbolSql,
                    Timestamp("fromInclusive", windowFrom),
                    Timestamp("toExclusive", bySymbolTo),
                    Integer("fetchLimit", 51))
            };

            foreach (var plan in plans)
            {
                Assert.Empty(plan.FactSequentialScans);
            }

            var serverVersion = await GetServerVersionAsync(connection);
            var report = BuildReport(
                serverVersion,
                windowMedianMs,
                seriesMedianMs,
                bySymbolMedianMs,
                plans);

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
            INSERT INTO "Orders" (
                "Id", "ExchangeOrderId", "ClientOrderId", "Symbol", "Side", "OrderType",
                "RequestedQuantity", "FilledQuantity", "AverageFillPrice", "Price", "Status",
                "CreatedAt", "UpdatedAt")
            SELECT
                md5('metrics-plan-order-' || g::text)::uuid,
                NULL,
                'metrics-plan-order-' || g::text,
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
                'Market',
                1.0,
                0.0,
                NULL,
                NULL,
                'Accepted',
                TIMESTAMPTZ '2026-09-01 00:00:00+00' + ((g - 1) * INTERVAL '120 seconds'),
                TIMESTAMPTZ '2026-09-01 00:00:00+00' + ((g - 1) * INTERVAL '120 seconds')
            FROM generate_series(1, {{OrderCount}}) AS g;

            INSERT INTO "TradingSignals" (
                "Id", "Symbol", "Side", "SignalType", "RequestedQuantity", "RiskPercent",
                "StopLoss", "TakeProfit", "CreatedAt", "Source", "Outcome",
                "RiskRejectionReasons", "OrderId", "ClientOrderId")
            SELECT
                md5('metrics-plan-signal-' || g::text)::uuid,
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
                'MetricsPlan',
                1.0,
                NULL,
                NULL,
                NULL,
                TIMESTAMPTZ '2026-09-01 00:00:00+00' + ((g - 1) * INTERVAL '60 seconds'),
                'MetricsPlanFixture',
                CASE g % 3
                    WHEN 0 THEN 'Accepted'
                    WHEN 1 THEN 'Rejected'
                    ELSE 'Received'
                END,
                ARRAY[]::text[],
                NULL,
                NULL
            FROM generate_series(1, {{SignalCount}}) AS g;

            INSERT INTO "OrderLifecycleEvents" (
                "Id", "OrderId", "ClientOrderId", "PreviousStatus", "Status",
                "FilledQuantity", "AverageFillPrice", "ExchangeOrderId", "Source", "OccurredAt")
            SELECT
                md5('metrics-plan-lifecycle-' || g::text)::uuid,
                md5('metrics-plan-order-' || ((((g - 1) % {{OrderCount}}) + 1)::text))::uuid,
                'metrics-plan-order-' || (((g - 1) % {{OrderCount}}) + 1)::text,
                NULL,
                CASE g % 4
                    WHEN 0 THEN 'Created'
                    WHEN 1 THEN 'Submitted'
                    WHEN 2 THEN 'Accepted'
                    ELSE 'Filled'
                END,
                0.0,
                NULL,
                NULL,
                'MetricsPlanFixture',
                TIMESTAMPTZ '2026-09-01 00:00:00+00' + ((g - 1) * INTERVAL '40 seconds')
            FROM generate_series(1, {{LifecycleCount}}) AS g;

            INSERT INTO "Fills" (
                "Id", "OrderId", "ExchangeFillId", "Quantity", "Price", "Fee", "FeeCurrency", "FilledAt")
            SELECT
                md5('metrics-plan-fill-' || g::text)::uuid,
                md5('metrics-plan-order-' || g::text)::uuid,
                'metrics-plan-fill-' || g::text,
                1.0,
                100.0 + (g % 100),
                NULL,
                NULL,
                TIMESTAMPTZ '2026-09-01 00:00:00+00' + ((g - 1) * INTERVAL '120 seconds')
            FROM generate_series(1, {{FillCount}}) AS g;

            ANALYZE "Orders";
            ANALYZE "TradingSignals";
            ANALYZE "OrderLifecycleEvents";
            ANALYZE "Fills";
            """);
    }

    private static async Task<double> MeasureMedianAsync(Func<Task> action)
    {
        var samples = new double[5];
        for (var i = 0; i < samples.Length; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            await action();
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        return samples[samples.Length / 2];
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
        var factSequentialScans = new SortedSet<string>(StringComparer.Ordinal);
        CollectPlanFacts(plan, indexes, factSequentialScans);

        return new PlanSummary(
            name,
            root.GetProperty("Planning Time").GetDouble(),
            root.GetProperty("Execution Time").GetDouble(),
            plan.TryGetProperty("Actual Rows", out var actualRows) ? actualRows.GetDouble() : 0,
            plan.TryGetProperty("Shared Hit Blocks", out var sharedHitBlocks) ? sharedHitBlocks.GetInt64() : 0,
            plan.TryGetProperty("Shared Read Blocks", out var sharedReadBlocks) ? sharedReadBlocks.GetInt64() : 0,
            indexes.ToArray(),
            factSequentialScans.ToArray());
    }

    private static void CollectPlanFacts(
        JsonElement node,
        ISet<string> indexes,
        ISet<string> factSequentialScans)
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
            relation is "TradingSignals" or "OrderLifecycleEvents" or "Fills")
        {
            factSequentialScans.Add(relation);
        }

        if (!node.TryGetProperty("Plans", out var plansElement))
        {
            return;
        }

        foreach (var child in plansElement.EnumerateArray())
        {
            CollectPlanFacts(child, indexes, factSequentialScans);
        }
    }

    private static async Task<string> GetServerVersionAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SHOW server_version;", connection);
        return (string)(await command.ExecuteScalarAsync() ?? "unknown");
    }

    private static string BuildReport(
        string serverVersion,
        double windowMedianMs,
        double seriesMedianMs,
        double bySymbolMedianMs,
        IReadOnlyList<PlanSummary> plans)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## TradeOps execution-metrics query-plan baseline");
        builder.AppendLine();
        builder.AppendLine($"- PostgreSQL: `{serverVersion}`");
        builder.AppendLine($"- Fixture: `{OrderCount:N0}` orders, `{SignalCount:N0}` signals, `{LifecycleCount:N0}` lifecycle events, `{FillCount:N0}` fills");
        builder.AppendLine("- Distribution: deterministic 28-day time range, 8 symbols");
        builder.AppendLine("- Measurements are CI-fixture observations, not a production SLO.");
        builder.AppendLine();
        builder.AppendLine("### Repository median latency (5 measured calls after warm-up)");
        builder.AppendLine();
        builder.AppendLine("| Query | Median ms |");
        builder.AppendLine("| --- | ---: |");
        builder.AppendLine($"| window: 1h, BTCUSDT | {windowMedianMs:F2} |");
        builder.AppendLine($"| series: 6h, 5m, BTCUSDT | {seriesMedianMs:F2} |");
        builder.AppendLine($"| by-symbol: 24h, limit 50 | {bySymbolMedianMs:F2} |");
        builder.AppendLine();
        builder.AppendLine("### EXPLAIN (ANALYZE, BUFFERS)");
        builder.AppendLine();
        builder.AppendLine("| Plan | Planning ms | Execution ms | Root rows | Shared hit | Shared read | Indexes | Fact seq scans |");
        builder.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | --- | --- |");

        foreach (var plan in plans)
        {
            var indexes = plan.Indexes.Count == 0 ? "none" : string.Join(", ", plan.Indexes);
            var seqScans = plan.FactSequentialScans.Count == 0
                ? "none"
                : string.Join(", ", plan.FactSequentialScans);
            builder.AppendLine(
                $"| {plan.Name} | {plan.PlanningMs:F2} | {plan.ExecutionMs:F2} | {plan.RootActualRows:F0} | {plan.SharedHitBlocks} | {plan.SharedReadBlocks} | {indexes} | {seqScans} |");
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
                Path.Combine(workspace, "metrics-query-plan-report.md"),
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
        IReadOnlyList<string> FactSequentialScans);

    private const string WindowSignalsSql = """
        SELECT signal."Outcome", COUNT(*)
        FROM "TradingSignals" AS signal
        WHERE signal."CreatedAt" >= @fromInclusive
          AND signal."CreatedAt" < @toExclusive
          AND signal."Symbol" = @symbol
        GROUP BY signal."Outcome"
        """;

    private const string WindowLifecycleSql = """
        SELECT lifecycle."Status", COUNT(*)
        FROM "OrderLifecycleEvents" AS lifecycle
        JOIN "Orders" AS order_row ON order_row."Id" = lifecycle."OrderId"
        WHERE lifecycle."OccurredAt" >= @fromInclusive
          AND lifecycle."OccurredAt" < @toExclusive
          AND order_row."Symbol" = @symbol
        GROUP BY lifecycle."Status"
        """;

    private const string WindowOrdersTouchedSql = """
        SELECT COUNT(*)
        FROM (
            SELECT DISTINCT lifecycle."OrderId"
            FROM "OrderLifecycleEvents" AS lifecycle
            JOIN "Orders" AS order_row ON order_row."Id" = lifecycle."OrderId"
            WHERE lifecycle."OccurredAt" >= @fromInclusive
              AND lifecycle."OccurredAt" < @toExclusive
              AND order_row."Symbol" = @symbol
        ) AS touched
        """;

    private const string WindowFillsSql = """
        SELECT COUNT(*)
        FROM "Fills" AS fill
        JOIN "Orders" AS order_row ON order_row."Id" = fill."OrderId"
        WHERE fill."FilledAt" >= @fromInclusive
          AND fill."FilledAt" < @toExclusive
          AND order_row."Symbol" = @symbol
        """;

    private const string SeriesSql = """
        WITH buckets AS (
            SELECT bucket_start
            FROM generate_series(
                date_bin(@bucketSize, @fromInclusive, TIMESTAMPTZ '1970-01-01 00:00:00+00'),
                @toExclusive,
                @bucketSize) AS bucket_start
            WHERE bucket_start < @toExclusive
        ),
        signal_counts AS (
            SELECT
                date_bin(@bucketSize, signal."CreatedAt", TIMESTAMPTZ '1970-01-01 00:00:00+00') AS bucket_start,
                COUNT(*) FILTER (WHERE signal."Outcome" = 'Accepted') AS accepted,
                COUNT(*) FILTER (WHERE signal."Outcome" = 'Rejected') AS rejected,
                COUNT(*) FILTER (WHERE signal."Outcome" = 'Received') AS pending
            FROM "TradingSignals" AS signal
            WHERE signal."CreatedAt" >= @fromInclusive
              AND signal."CreatedAt" < @toExclusive
              AND signal."Symbol" = @symbol
            GROUP BY 1
        ),
        lifecycle_counts AS (
            SELECT
                date_bin(@bucketSize, lifecycle."OccurredAt", TIMESTAMPTZ '1970-01-01 00:00:00+00') AS bucket_start,
                COUNT(*) AS events,
                COUNT(DISTINCT lifecycle."OrderId") AS orders_touched,
                COUNT(*) FILTER (WHERE lifecycle."Status" = 'Created') AS created,
                COUNT(*) FILTER (WHERE lifecycle."Status" = 'Submitted') AS submitted,
                COUNT(*) FILTER (WHERE lifecycle."Status" = 'Accepted') AS accepted,
                COUNT(*) FILTER (WHERE lifecycle."Status" = 'PartiallyFilled') AS partially_filled,
                COUNT(*) FILTER (WHERE lifecycle."Status" = 'Filled') AS filled,
                COUNT(*) FILTER (WHERE lifecycle."Status" = 'Cancelled') AS cancelled,
                COUNT(*) FILTER (WHERE lifecycle."Status" = 'Rejected') AS rejected,
                COUNT(*) FILTER (WHERE lifecycle."Status" = 'Unknown') AS unknown
            FROM "OrderLifecycleEvents" AS lifecycle
            JOIN "Orders" AS lifecycle_order ON lifecycle_order."Id" = lifecycle."OrderId"
            WHERE lifecycle."OccurredAt" >= @fromInclusive
              AND lifecycle."OccurredAt" < @toExclusive
              AND lifecycle_order."Symbol" = @symbol
            GROUP BY 1
        ),
        fill_counts AS (
            SELECT
                date_bin(@bucketSize, fill."FilledAt", TIMESTAMPTZ '1970-01-01 00:00:00+00') AS bucket_start,
                COUNT(*) AS fills_received
            FROM "Fills" AS fill
            JOIN "Orders" AS fill_order ON fill_order."Id" = fill."OrderId"
            WHERE fill."FilledAt" >= @fromInclusive
              AND fill."FilledAt" < @toExclusive
              AND fill_order."Symbol" = @symbol
            GROUP BY 1
        )
        SELECT
            GREATEST(bucket.bucket_start, @fromInclusive),
            LEAST(bucket.bucket_start + @bucketSize, @toExclusive),
            COALESCE(signal.accepted, 0),
            COALESCE(signal.rejected, 0),
            COALESCE(signal.pending, 0),
            COALESCE(lifecycle.events, 0),
            COALESCE(lifecycle.orders_touched, 0),
            COALESCE(lifecycle.created, 0),
            COALESCE(lifecycle.submitted, 0),
            COALESCE(lifecycle.accepted, 0),
            COALESCE(lifecycle.partially_filled, 0),
            COALESCE(lifecycle.filled, 0),
            COALESCE(lifecycle.cancelled, 0),
            COALESCE(lifecycle.rejected, 0),
            COALESCE(lifecycle.unknown, 0),
            COALESCE(fill.fills_received, 0)
        FROM buckets AS bucket
        LEFT JOIN signal_counts AS signal ON signal.bucket_start = bucket.bucket_start
        LEFT JOIN lifecycle_counts AS lifecycle ON lifecycle.bucket_start = bucket.bucket_start
        LEFT JOIN fill_counts AS fill ON fill.bucket_start = bucket.bucket_start
        ORDER BY bucket.bucket_start
        """;

    private const string BySymbolSql = """
        WITH signal_counts AS (
            SELECT
                signal."Symbol" AS symbol,
                COUNT(*) FILTER (WHERE signal."Outcome" = 'Accepted') AS accepted_signals,
                COUNT(*) FILTER (WHERE signal."Outcome" = 'Rejected') AS rejected_signals,
                COUNT(*) FILTER (WHERE signal."Outcome" = 'Received') AS pending_signals
            FROM "TradingSignals" AS signal
            WHERE signal."CreatedAt" >= @fromInclusive
              AND signal."CreatedAt" < @toExclusive
            GROUP BY signal."Symbol"
        ),
        lifecycle_counts AS (
            SELECT
                order_row."Symbol" AS symbol,
                COUNT(*) AS lifecycle_events,
                COUNT(DISTINCT lifecycle."OrderId") AS orders_touched,
                COUNT(*) FILTER (WHERE lifecycle."Status" = 'Created') AS created,
                COUNT(*) FILTER (WHERE lifecycle."Status" = 'Submitted') AS submitted,
                COUNT(*) FILTER (WHERE lifecycle."Status" = 'Accepted') AS accepted,
                COUNT(*) FILTER (WHERE lifecycle."Status" = 'PartiallyFilled') AS partially_filled,
                COUNT(*) FILTER (WHERE lifecycle."Status" = 'Filled') AS filled,
                COUNT(*) FILTER (WHERE lifecycle."Status" = 'Cancelled') AS cancelled,
                COUNT(*) FILTER (WHERE lifecycle."Status" = 'Rejected') AS rejected,
                COUNT(*) FILTER (WHERE lifecycle."Status" = 'Unknown') AS unknown
            FROM "OrderLifecycleEvents" AS lifecycle
            JOIN "Orders" AS order_row ON order_row."Id" = lifecycle."OrderId"
            WHERE lifecycle."OccurredAt" >= @fromInclusive
              AND lifecycle."OccurredAt" < @toExclusive
            GROUP BY order_row."Symbol"
        ),
        fill_counts AS (
            SELECT
                order_row."Symbol" AS symbol,
                COUNT(*) AS fills_received
            FROM "Fills" AS fill
            JOIN "Orders" AS order_row ON order_row."Id" = fill."OrderId"
            WHERE fill."FilledAt" >= @fromInclusive
              AND fill."FilledAt" < @toExclusive
            GROUP BY order_row."Symbol"
        ),
        combined AS (
            SELECT
                COALESCE(signal.symbol, lifecycle.symbol, fill.symbol) AS symbol,
                COALESCE(signal.accepted_signals, 0) AS accepted_signals,
                COALESCE(signal.rejected_signals, 0) AS rejected_signals,
                COALESCE(signal.pending_signals, 0) AS pending_signals,
                COALESCE(lifecycle.lifecycle_events, 0) AS lifecycle_events,
                COALESCE(lifecycle.orders_touched, 0) AS orders_touched,
                COALESCE(lifecycle.created, 0) AS created,
                COALESCE(lifecycle.submitted, 0) AS submitted,
                COALESCE(lifecycle.accepted, 0) AS accepted,
                COALESCE(lifecycle.partially_filled, 0) AS partially_filled,
                COALESCE(lifecycle.filled, 0) AS filled,
                COALESCE(lifecycle.cancelled, 0) AS cancelled,
                COALESCE(lifecycle.rejected, 0) AS rejected,
                COALESCE(lifecycle.unknown, 0) AS unknown,
                COALESCE(fill.fills_received, 0) AS fills_received
            FROM signal_counts AS signal
            FULL OUTER JOIN lifecycle_counts AS lifecycle ON lifecycle.symbol = signal.symbol
            FULL OUTER JOIN fill_counts AS fill ON fill.symbol = COALESCE(signal.symbol, lifecycle.symbol)
        )
        SELECT
            symbol,
            accepted_signals,
            rejected_signals,
            pending_signals,
            lifecycle_events,
            orders_touched,
            created,
            submitted,
            accepted,
            partially_filled,
            filled,
            cancelled,
            rejected,
            unknown,
            fills_received
        FROM combined
        ORDER BY
            accepted_signals + rejected_signals + pending_signals + lifecycle_events + fills_received DESC,
            symbol ASC
        LIMIT @fetchLimit
        """;
}
