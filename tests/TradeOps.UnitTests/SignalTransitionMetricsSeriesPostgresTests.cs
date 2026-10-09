using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class SignalTransitionMetricsSeriesPostgresTests
{
    [Fact]
    public async Task GetSeriesAsync_BucketsOutcomeEventsByOccurredAtAndEmitsEmptyBuckets()
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

        var databaseName = $"tradeops_signal_transition_series_{Guid.NewGuid():N}";
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

            var fromInclusive = new DateTimeOffset(2026, 10, 1, 10, 2, 0, TimeSpan.Zero);
            var toExclusive = new DateTimeOffset(2026, 10, 1, 10, 13, 0, TimeSpan.Zero);

            var btcAccepted = NewSignal("BTCUSDT", fromInclusive.AddHours(-2), SignalOutcome.Accepted);
            var btcRejected = NewSignal("BTCUSDT", fromInclusive.AddHours(-1), SignalOutcome.Rejected);
            var ethAccepted = NewSignal("ETHUSDT", fromInclusive.AddHours(-3), SignalOutcome.Accepted);
            var upperBound = NewSignal("BTCUSDT", fromInclusive, SignalOutcome.Accepted);
            var beforeLowerBound = NewSignal("BTCUSDT", fromInclusive, SignalOutcome.Accepted);
            var legacyNoHistory = NewSignal("BTCUSDT", fromInclusive, SignalOutcome.Accepted);

            dbContext.TradingSignals.AddRange(
                btcAccepted,
                btcRejected,
                ethAccepted,
                upperBound,
                beforeLowerBound,
                legacyNoHistory);

            dbContext.TradingSignalOutcomeEvents.AddRange(
                NewEvent(beforeLowerBound.Id, SignalOutcome.Received, fromInclusive.AddSeconds(-1)),
                NewEvent(btcAccepted.Id, SignalOutcome.Received, fromInclusive.AddSeconds(30)),
                NewEvent(btcAccepted.Id, SignalOutcome.Accepted, fromInclusive.AddMinutes(2)),
                NewEvent(btcRejected.Id, SignalOutcome.Received, new DateTimeOffset(2026, 10, 1, 10, 7, 0, TimeSpan.Zero)),
                NewEvent(btcRejected.Id, SignalOutcome.Rejected, new DateTimeOffset(2026, 10, 1, 10, 8, 0, TimeSpan.Zero)),
                NewEvent(ethAccepted.Id, SignalOutcome.Received, new DateTimeOffset(2026, 10, 1, 10, 7, 30, TimeSpan.Zero)),
                NewEvent(ethAccepted.Id, SignalOutcome.Accepted, new DateTimeOffset(2026, 10, 1, 10, 8, 30, TimeSpan.Zero)),
                NewEvent(upperBound.Id, SignalOutcome.Accepted, toExclusive));

            await dbContext.SaveChangesAsync();

            var repository = new EfSignalTransitionMetricsRepository(dbContext);

            var snapshot = await repository.GetSeriesAsync(
                fromInclusive,
                toExclusive,
                "5m",
                TimeSpan.FromMinutes(5),
                "BTCUSDT");

            Assert.Equal(fromInclusive, snapshot.FromInclusive);
            Assert.Equal(toExclusive, snapshot.ToExclusive);
            Assert.Equal("5m", snapshot.Bucket);
            Assert.Equal("BTCUSDT", snapshot.Symbol);
            Assert.Equal(3, snapshot.Buckets.Count);

            var first = snapshot.Buckets[0];
            Assert.Equal(fromInclusive, first.FromInclusive);
            Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 5, 0, TimeSpan.Zero), first.ToExclusive);
            Assert.Equal(1, first.ReceivedTransitions);
            Assert.Equal(1, first.AcceptedTransitions);
            Assert.Equal(0, first.RejectedTransitions);

            var second = snapshot.Buckets[1];
            Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 5, 0, TimeSpan.Zero), second.FromInclusive);
            Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 10, 0, TimeSpan.Zero), second.ToExclusive);
            Assert.Equal(1, second.ReceivedTransitions);
            Assert.Equal(0, second.AcceptedTransitions);
            Assert.Equal(1, second.RejectedTransitions);

            var third = snapshot.Buckets[2];
            Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 10, 0, TimeSpan.Zero), third.FromInclusive);
            Assert.Equal(toExclusive, third.ToExclusive);
            Assert.Equal(0, third.ReceivedTransitions);
            Assert.Equal(0, third.AcceptedTransitions);
            Assert.Equal(0, third.RejectedTransitions);

            var allSymbols = await repository.GetSeriesAsync(
                fromInclusive,
                toExclusive,
                "5m",
                TimeSpan.FromMinutes(5));

            Assert.Equal(2, allSymbols.Buckets[1].ReceivedTransitions);
            Assert.Equal(1, allSymbols.Buckets[1].AcceptedTransitions);
            Assert.Equal(1, allSymbols.Buckets[1].RejectedTransitions);
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
        string symbol,
        DateTimeOffset createdAt,
        SignalOutcome outcome) =>
        new()
        {
            Id = Guid.NewGuid(),
            Symbol = symbol,
            Side = OrderSide.Buy,
            SignalType = "External",
            RequestedQuantity = 1m,
            CreatedAt = createdAt,
            Source = "SignalTransitionSeriesTest",
            Outcome = outcome
        };

    private static TradingSignalOutcomeEvent NewEvent(
        Guid signalId,
        SignalOutcome outcome,
        DateTimeOffset occurredAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            TradingSignalId = signalId,
            PreviousOutcome = outcome == SignalOutcome.Received ? null : SignalOutcome.Received,
            Outcome = outcome,
            OccurredAt = occurredAt,
            RiskRejectionReasons = outcome == SignalOutcome.Rejected
                ? ["Representative rejection"]
                : []
        };
}
