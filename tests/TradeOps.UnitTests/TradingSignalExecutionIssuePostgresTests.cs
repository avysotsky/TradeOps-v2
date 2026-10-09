using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class TradingSignalExecutionIssuePostgresTests
{
    [Fact]
    public async Task UpdateAsync_PersistsAndClearsExecutionIssueWithoutAppendingOutcomeTransition()
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

        var databaseName = $"tradeops_signal_execution_issue_{Guid.NewGuid():N}";
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

            var repository = new EfTradingSignalRepository(dbContext);
            var signal = new TradingSignal
            {
                Id = Guid.NewGuid(),
                Symbol = "BTCUSDT",
                Side = OrderSide.Buy,
                SignalType = "External",
                RequestedQuantity = 0.001m,
                CreatedAt = DateTimeOffset.UtcNow,
                Source = "ExecutionIssuePostgresTest"
            };

            Assert.True(await repository.TryAddAsync(signal));
            Assert.Equal(1, await dbContext.TradingSignalOutcomeEvents.CountAsync());

            signal.ExecutionIssueCode = "ClientOrderIdConflict";
            signal.ExecutionIssueMessage = "Representative deterministic order identity conflict.";
            signal.ExecutionIssueAt = DateTimeOffset.UtcNow;

            await repository.UpdateAsync(signal);

            dbContext.ChangeTracker.Clear();
            var persistedIssue = await dbContext.TradingSignals.SingleAsync(item => item.Id == signal.Id);
            Assert.Equal(SignalOutcome.Received, persistedIssue.Outcome);
            Assert.Equal("ClientOrderIdConflict", persistedIssue.ExecutionIssueCode);
            Assert.Equal(signal.ExecutionIssueMessage, persistedIssue.ExecutionIssueMessage);
            Assert.NotNull(persistedIssue.ExecutionIssueAt);
            Assert.Equal(1, await dbContext.TradingSignalOutcomeEvents.CountAsync());

            persistedIssue.ExecutionIssueCode = null;
            persistedIssue.ExecutionIssueMessage = null;
            persistedIssue.ExecutionIssueAt = null;
            await repository.UpdateAsync(persistedIssue);

            dbContext.ChangeTracker.Clear();
            var cleared = await dbContext.TradingSignals.SingleAsync(item => item.Id == signal.Id);
            Assert.Equal(SignalOutcome.Received, cleared.Outcome);
            Assert.Null(cleared.ExecutionIssueCode);
            Assert.Null(cleared.ExecutionIssueMessage);
            Assert.Null(cleared.ExecutionIssueAt);
            Assert.Equal(1, await dbContext.TradingSignalOutcomeEvents.CountAsync());
        }
        finally
        {
            await using var dropDatabase = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
                adminConnection);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }
}
