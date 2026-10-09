using System.Diagnostics;
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

public sealed class SignalTransitionMetricsQueryPlanPostgresTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset FixtureStart =
        new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private const int SignalCount = 40_000;
    private const int EventCount = SignalCount * 2;

    [Fact]
    public async Task RepresentativeVolume_RecordsTransitionMetricAccessPlans()
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

        var databaseName = $"tradeops_signal_transition_plan_{Guid.NewGuid():N}";
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
            const string symbol = "BTCUSDT";

            var repository = new EfSignalTransitionMetricsRepository(dbContext);

            var allWarmup = await repository.GetWindowAsync(windowFrom, windowTo);
            var symbolWarmup = await repository.GetWindowAsync(windowFrom, windowTo, symbol);
            Assert.True(
                allWarmup.ReceivedTransitions +
                allWarmup.AcceptedTransitions +
                allWarmup.RejectedTransitions > 0);
            Assert.True(
                symbolWarmup.ReceivedTransitions +
                symbolWarmup.AcceptedTransitions +
                symbolWarmup.RejectedTransitions > 0);

            var allMedianMs = await MeasureMedianAsync(async () =>
            {
                _ = await repository.GetWindowAsync(windowFrom, windowTo);
            });
            var symbolMedianMs = await MeasureMedianAsync(async () =>
            {
                _ = await repository.GetWindowAsync(windowFrom, windowTo, symbol);
            });

            await using var connection = new NpgsqlConnection(testBuilder.ConnectionString);
            await connection.OpenAsync();

            var plans = new[]
            {
                await ExplainAsync(
                    connection,
                    "transition-window-unscoped",
                    UnscopedWindowSql,
                    Timestamp("fromInclusive", windowFrom),
                    Timestamp("toExclusive", windowTo)),
                await ExplainAsync(
                    connection,
                    "transition-window-symbol-scoped",
                    SymbolScopedWindowSql,
                    Timestamp("fromInclusive", windowFrom),
                    Timestamp("toExclusive", windowTo),
                    Varchar("symbol", symbol))
            };

            var serverVersion = await GetServerVersionAsync(connection);
            var report = BuildReport(serverVersion, allMedianMs, symbolMedianMs, plans);

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
                md5('transition-plan-signal-' || g::text)::uuid,
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
                'TransitionPlan',
                1.0,
                NULL,
                NULL,
                NULL,
                TIMESTAMPTZ '2026-09-01 00:00:00+00' + ((g - 1) * INTERVAL '60 seconds'),
                'TransitionPlanFixture',
                CASE WHEN g % 2 = 0 THEN 'Accepted' ELSE 'Rejected' END,
                ARRAY[]::text[],
                NULL,
                NULL
            FROM generate_series(1, {{SignalCount}}) AS g;

            INSERT INTO "TradingSignalOutcomeEvents" (
                "Id", "TradingSignalId", "PreviousOutcome", "Outcome", "OccurredAt",
                "RiskRejectionReasons", "OrderId", "ClientOrderId")
            SELECT
                md5('transition-plan-received-' || g::text)::uuid,
                md5('transition-plan-signal-' || g::text)::uuid,
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
                md5('transition-plan-terminal-' || g::text)::uuid,
                md5('transition-plan-signal-' || g::text)::uuid,
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
        var transitionSequentialScan = false;
        CollectPlanFacts(plan, indexes, ref transitionSequentialScan);

        return new PlanSummary(
            name,
            root.GetProperty("Planning Time").GetDouble(),
            root.GetProperty("Execution Time").GetDouble(),
            plan.TryGetProperty("Actual Rows", out var actualRows) ? actualRows.GetDouble() : 0,
            plan.TryGetProperty("Shared Hit Blocks", out var sharedHitBlocks) ? sharedHitBlocks.GetInt64() : 0,
            plan.TryGetProperty("Shared Read Blocks", out var sharedReadBlocks) ? sharedReadBlocks.GetInt64() : 0,
            indexes.ToArray(),
            transitionSequentialScan);
    }

    private static void CollectPlanFacts(
        JsonElement node,
        ISet<string> indexes,
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

        if (string.Equals(nodeType, "Seq Scan", StringComparison.Ordinal) &&
            string.Equals(relation, "TradingSignalOutcomeEvents", StringComparison.Ordinal))
        {
            transitionSequentialScan = true;
        }

        if (!node.TryGetProperty("Plans", out var plansElement))
        {
            return;
        }

        foreach (var child in plansElement.EnumerateArray())
        {
            CollectPlanFacts(child, indexes, ref transitionSequentialScan);
        }
    }

    private static async Task<string> GetServerVersionAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SHOW server_version;", connection);
        return (string)(await command.ExecuteScalarAsync() ?? "unknown");
    }

    private static string BuildReport(
        string serverVersion,
        double allMedianMs,
        double symbolMedianMs,
        IReadOnlyList<PlanSummary> plans)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## TradeOps signal-transition query-plan baseline");
        builder.AppendLine();
        builder.AppendLine($"- PostgreSQL: `{serverVersion}`");
        builder.AppendLine($"- Fixture: `{SignalCount:N0}` signals, `{EventCount:N0}` persisted outcome events");
        builder.AppendLine("- Distribution: deterministic ~28-day time range, 8 symbols, two outcome events per signal");
        builder.AppendLine("- Measurements are CI-fixture observations, not a production SLO.");
        builder.AppendLine();
        builder.AppendLine("### Repository median latency (5 measured calls after warm-up)");
        builder.AppendLine();
        builder.AppendLine("| Query | Median ms |");
        builder.AppendLine("| --- | ---: |");
        builder.AppendLine($"| transition window: 1h, all symbols | {allMedianMs:F2} |");
        builder.AppendLine($"| transition window: 1h, BTCUSDT | {symbolMedianMs:F2} |");
        builder.AppendLine();
        builder.AppendLine("### EXPLAIN (ANALYZE, BUFFERS)");
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
                Path.Combine(workspace, "signal-transition-query-plan-report.md"),
                report);
        }
    }

    private static NpgsqlParameter Timestamp(string name, DateTimeOffset value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = value };

    private static NpgsqlParameter Varchar(string name, string value) =>
        new(name, NpgsqlDbType.Varchar) { Value = value };

    private sealed record PlanSummary(
        string Name,
        double PlanningMs,
        double ExecutionMs,
        double RootActualRows,
        long SharedHitBlocks,
        long SharedReadBlocks,
        IReadOnlyList<string> Indexes,
        bool TransitionSequentialScan);

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
