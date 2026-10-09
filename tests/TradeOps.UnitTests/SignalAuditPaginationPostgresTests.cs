using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class SignalAuditPaginationPostgresTests
{
    [Fact]
    public async Task GetSignalsPageAsync_KeysetTraversal_DoesNotDuplicateOrSkipEqualTimestampRows()
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

        var databaseName = $"tradeops_signal_audit_pagination_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = "postgres",
            Pooling = false
        };
        var testBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
        {
            Database = databaseName,
            Pooling = false
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
                .UseNpgsql(testBuilder.ConnectionString)
                .Options;

            await using var dbContext = new TradeOpsDbContext(options);
            await dbContext.Database.MigrateAsync();

            var t0 = DateTimeOffset.Parse("2026-10-02T10:00:00Z");
            var t1 = t0.AddMinutes(1);
            var t2 = t0.AddMinutes(2);
            var t3 = t0.AddMinutes(3);

            var signals = new[]
            {
                NewSignal(1, t0),
                NewSignal(2, t0),
                NewSignal(3, t1),
                NewSignal(4, t2),
                NewSignal(5, t2),
                NewSignal(6, t2),
                NewSignal(7, t3)
            };

            dbContext.TradingSignals.AddRange(signals);
            await dbContext.SaveChangesAsync();

            var repository = new EfOperatorReadRepository(dbContext);
            var seen = new List<Guid>();
            SignalAuditCursorPosition? cursor = null;
            var pageCount = 0;

            do
            {
                var page = await repository.GetSignalsPageAsync(
                    symbol: "BTCUSDT",
                    outcome: SignalOutcome.Received,
                    executionIssueCode: null,
                    fromInclusive: t0,
                    toExclusive: t3.AddMilliseconds(1),
                    cursor,
                    limit: 2);

                pageCount++;
                Assert.InRange(page.Signals.Count, 1, 2);
                Assert.DoesNotContain(page.Signals, signal => seen.Contains(signal.Id));
                seen.AddRange(page.Signals.Select(signal => signal.Id));

                cursor = page.HasMore
                    ? new SignalAuditCursorPosition(
                        page.Signals.Last().CreatedAt,
                        page.Signals.Last().Id)
                    : null;

                if (!page.HasMore)
                {
                    break;
                }
            }
            while (pageCount < 10);

            Assert.Equal(4, pageCount);
            Assert.Equal(
                signals
                    .OrderByDescending(signal => signal.CreatedAt)
                    .ThenByDescending(signal => signal.Id)
                    .Select(signal => signal.Id)
                    .ToArray(),
                seen.ToArray());
            Assert.Equal(signals.Length, seen.Distinct().Count());
        }
        finally
        {
            await using var dropDatabase = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
                adminConnection);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    private static TradingSignal NewSignal(int idSuffix, DateTimeOffset createdAt) =>
        new()
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{idSuffix:000000000000}"),
            Symbol = "BTCUSDT",
            Side = OrderSide.Buy,
            SignalType = "External",
            RequestedQuantity = 0.001m,
            CreatedAt = createdAt,
            Source = "SignalAuditPaginationPostgresTest",
            Outcome = SignalOutcome.Received
        };
}
