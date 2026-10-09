using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class SignalTransitionMetricsPostgresTests
{
    [Fact]
    public async Task Repository_UsesOutcomeEventTimeAndOptionalSymbolScope()
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

        var databaseName = $"tradeops_signal_transition_metrics_{Guid.NewGuid():N}";
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

            var btcAccepted = NewSignal(
                Guid.Parse("11111111-1111-4111-8111-111111111111"),
                "BTCUSDT",
                from.AddHours(-2),
                SignalOutcome.Accepted);
            var ethRejected = NewSignal(
                Guid.Parse("22222222-2222-4222-8222-222222222222"),
                "ETHUSDT",
                from.AddMinutes(5),
                SignalOutcome.Rejected);
            var btcRejected = NewSignal(
                Guid.Parse("33333333-3333-4333-8333-333333333333"),
                "BTCUSDT",
                from.AddMinutes(10),
                SignalOutcome.Rejected);
            var legacyNoHistory = NewSignal(
                Guid.Parse("44444444-4444-4444-8444-444444444444"),
                "BTCUSDT",
                from.AddMinutes(20),
                SignalOutcome.Accepted);

            dbContext.TradingSignals.AddRange(
                btcAccepted,
                ethRejected,
                btcRejected,
                legacyNoHistory);

            dbContext.TradingSignalOutcomeEvents.AddRange(
                NewEvent(btcAccepted.Id, SignalOutcome.Received, from.AddHours(-1)),
                NewEvent(btcAccepted.Id, SignalOutcome.Accepted, from.AddMinutes(15), SignalOutcome.Received),
                NewEvent(ethRejected.Id, SignalOutcome.Received, from.AddMinutes(5)),
                NewEvent(ethRejected.Id, SignalOutcome.Rejected, from.AddMinutes(30), SignalOutcome.Received),
                NewEvent(btcRejected.Id, SignalOutcome.Received, from.AddMinutes(10)),
                NewEvent(btcRejected.Id, SignalOutcome.Rejected, to, SignalOutcome.Received));

            await dbContext.SaveChangesAsync();

            var repository = new EfSignalTransitionMetricsRepository(dbContext);

            var all = await repository.GetWindowAsync(from, to);
            Assert.Equal(2, all.ReceivedTransitions);
            Assert.Equal(1, all.AcceptedTransitions);
            Assert.Equal(1, all.RejectedTransitions);

            var btc = await repository.GetWindowAsync(from, to, "BTCUSDT");
            Assert.Equal(1, btc.ReceivedTransitions);
            Assert.Equal(1, btc.AcceptedTransitions);
            Assert.Equal(0, btc.RejectedTransitions);

            Assert.Equal(from, btc.FromInclusive);
            Assert.Equal(to, btc.ToExclusive);
            Assert.Equal("BTCUSDT", btc.Symbol);
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
        Guid id,
        string symbol,
        DateTimeOffset createdAt,
        SignalOutcome outcome) =>
        new()
        {
            Id = id,
            Symbol = symbol,
            Side = OrderSide.Buy,
            SignalType = "External",
            RequestedQuantity = 1m,
            CreatedAt = createdAt,
            Source = "SignalTransitionMetricsTest",
            Outcome = outcome
        };

    private static TradingSignalOutcomeEvent NewEvent(
        Guid signalId,
        SignalOutcome outcome,
        DateTimeOffset occurredAt,
        SignalOutcome? previousOutcome = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            TradingSignalId = signalId,
            PreviousOutcome = previousOutcome,
            Outcome = outcome,
            OccurredAt = occurredAt
        };
}
