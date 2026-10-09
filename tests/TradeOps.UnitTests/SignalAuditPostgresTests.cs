using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class SignalAuditPostgresTests
{
    [Fact]
    public async Task GetSignalsAsync_FiltersByCreationTimeSymbolOutcomeAndLimitDeterministically()
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

        var databaseName = $"tradeops_signal_audit_{Guid.NewGuid():N}";
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

            var from = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
            var to = DateTimeOffset.Parse("2026-10-01T11:00:00Z");

            var atFrom = NewSignal(
                "11111111-1111-4111-8111-111111111111",
                "BTCUSDT",
                SignalOutcome.Accepted,
                from);
            var btcAccepted = NewSignal(
                "22222222-2222-4222-8222-222222222222",
                "BTCUSDT",
                SignalOutcome.Accepted,
                from.AddMinutes(10));
            var ethRejected = NewSignal(
                "33333333-3333-4333-8333-333333333333",
                "ETHUSDT",
                SignalOutcome.Rejected,
                from.AddMinutes(20));
            var btcRejected = NewSignal(
                "44444444-4444-4444-8444-444444444444",
                "BTCUSDT",
                SignalOutcome.Rejected,
                from.AddMinutes(30));
            btcRejected.ExecutionIssueCode = "ClientOrderIdConflict";
            btcRejected.ExecutionIssueMessage = "Representative issue";
            btcRejected.ExecutionIssueAt = from.AddMinutes(31);
            ethRejected.ExecutionIssueCode = "OtherIssue";

            var before = NewSignal(
                "55555555-5555-4555-8555-555555555555",
                "BTCUSDT",
                SignalOutcome.Rejected,
                from.AddTicks(-1));
            var atTo = NewSignal(
                "66666666-6666-4666-8666-666666666666",
                "BTCUSDT",
                SignalOutcome.Accepted,
                to);

            dbContext.TradingSignals.AddRange(
                atFrom,
                btcAccepted,
                ethRejected,
                btcRejected,
                before,
                atTo);
            await dbContext.SaveChangesAsync();

            var repository = new EfOperatorReadRepository(dbContext);

            var window = await repository.GetSignalsAsync(
                symbol: null,
                outcome: null,
                fromInclusive: from,
                toExclusive: to,
                limit: 50);

            Assert.Equal(
                [btcRejected.Id, ethRejected.Id, btcAccepted.Id, atFrom.Id],
                window.Select(item => item.Id).ToArray());

            var btcRejectedOnly = await repository.GetSignalsAsync(
                symbol: " btcusdt ",
                outcome: SignalOutcome.Rejected,
                fromInclusive: from,
                toExclusive: to,
                limit: 50);

            var filtered = Assert.Single(btcRejectedOnly);
            Assert.Equal(btcRejected.Id, filtered.Id);

            var issueFiltered = await repository.GetSignalsAsync(
                symbol: null,
                outcome: null,
                executionIssueCode: " ClientOrderIdConflict ",
                fromInclusive: from,
                toExclusive: to,
                limit: 50);

            var issueSignal = Assert.Single(issueFiltered);
            Assert.Equal(btcRejected.Id, issueSignal.Id);
            Assert.Equal("ClientOrderIdConflict", issueSignal.ExecutionIssueCode);

            var issueAndOutcomeFiltered = await repository.GetSignalsAsync(
                symbol: "BTCUSDT",
                outcome: SignalOutcome.Rejected,
                executionIssueCode: "ClientOrderIdConflict",
                fromInclusive: from,
                toExclusive: to,
                limit: 50);

            Assert.Equal(btcRejected.Id, Assert.Single(issueAndOutcomeFiltered).Id);

            var limited = await repository.GetSignalsAsync(
                symbol: null,
                outcome: null,
                fromInclusive: from,
                toExclusive: to,
                limit: 2);

            Assert.Equal(
                [btcRejected.Id, ethRejected.Id],
                limited.Select(item => item.Id).ToArray());
        }
        finally
        {
            await using var dropDatabase = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
                adminConnection);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    private static TradingSignal NewSignal(
        string id,
        string symbol,
        SignalOutcome outcome,
        DateTimeOffset createdAt) =>
        new()
        {
            Id = Guid.Parse(id),
            Symbol = symbol,
            Side = OrderSide.Buy,
            SignalType = "External",
            RequestedQuantity = 1m,
            CreatedAt = createdAt,
            Source = "SignalAuditPostgresTest",
            Outcome = outcome
        };
}
