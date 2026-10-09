using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class SignalTransitionMetricsBySymbolPostgresTests
{
    [Fact]
    public async Task GetBySymbolAsync_AggregatesOrdersBoundsAndTruncatesDeterministically()
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

        var databaseName = $"tradeops_signal_transition_by_symbol_{Guid.NewGuid():N}";
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

            var btcAccepted = NewSignal("BTCUSDT", SignalOutcome.Accepted);
            var btcRejected = NewSignal("BTCUSDT", SignalOutcome.Rejected);
            var ethAccepted = NewSignal("ETHUSDT", SignalOutcome.Accepted);
            var solRejected = NewSignal("SOLUSDT", SignalOutcome.Rejected);
            var boundarySignal = NewSignal("ADAUSDT", SignalOutcome.Accepted);
            var legacyNoHistory = NewSignal("XRPUSDT", SignalOutcome.Accepted);

            dbContext.TradingSignals.AddRange(
                btcAccepted,
                btcRejected,
                ethAccepted,
                solRejected,
                boundarySignal,
                legacyNoHistory);

            dbContext.TradingSignalOutcomeEvents.AddRange(
                NewEvent(btcAccepted.Id, SignalOutcome.Received, from.AddMinutes(1)),
                NewEvent(btcAccepted.Id, SignalOutcome.Accepted, from.AddMinutes(2)),
                NewEvent(btcRejected.Id, SignalOutcome.Received, from.AddMinutes(3)),
                NewEvent(btcRejected.Id, SignalOutcome.Rejected, from.AddMinutes(4)),
                NewEvent(ethAccepted.Id, SignalOutcome.Received, from.AddMinutes(5)),
                NewEvent(ethAccepted.Id, SignalOutcome.Accepted, from.AddMinutes(6)),
                NewEvent(solRejected.Id, SignalOutcome.Received, from.AddMinutes(7)),
                NewEvent(solRejected.Id, SignalOutcome.Rejected, from.AddMinutes(8)),
                NewEvent(boundarySignal.Id, SignalOutcome.Received, from.AddSeconds(-1)),
                NewEvent(boundarySignal.Id, SignalOutcome.Accepted, to));

            await dbContext.SaveChangesAsync();

            var repository = new EfSignalTransitionMetricsRepository(dbContext);
            var snapshot = await repository.GetBySymbolAsync(from, to, 2);

            Assert.Equal(from, snapshot.FromInclusive);
            Assert.Equal(to, snapshot.ToExclusive);
            Assert.Equal(2, snapshot.Limit);
            Assert.True(snapshot.IsTruncated);
            Assert.Equal(2, snapshot.Symbols.Count);

            var btc = snapshot.Symbols[0];
            Assert.Equal("BTCUSDT", btc.Symbol);
            Assert.Equal(4, btc.TotalTransitions);
            Assert.Equal(2, btc.ReceivedTransitions);
            Assert.Equal(1, btc.AcceptedTransitions);
            Assert.Equal(1, btc.RejectedTransitions);

            var eth = snapshot.Symbols[1];
            Assert.Equal("ETHUSDT", eth.Symbol);
            Assert.Equal(2, eth.TotalTransitions);
            Assert.Equal(1, eth.ReceivedTransitions);
            Assert.Equal(1, eth.AcceptedTransitions);
            Assert.Equal(0, eth.RejectedTransitions);

            var complete = await repository.GetBySymbolAsync(from, to, 10);
            Assert.False(complete.IsTruncated);
            Assert.Equal(["BTCUSDT", "ETHUSDT", "SOLUSDT"], complete.Symbols.Select(item => item.Symbol).ToArray());
            Assert.DoesNotContain(complete.Symbols, item => item.Symbol == "ADAUSDT");
            Assert.DoesNotContain(complete.Symbols, item => item.Symbol == "XRPUSDT");
        }
        finally
        {
            await using var dropDatabase = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
                adminConnection);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    private static TradingSignal NewSignal(string symbol, SignalOutcome outcome) =>
        new()
        {
            Id = Guid.NewGuid(),
            Symbol = symbol,
            Side = OrderSide.Buy,
            SignalType = "External",
            RequestedQuantity = 1m,
            CreatedAt = DateTimeOffset.Parse("2026-09-30T00:00:00Z"),
            Source = "SignalTransitionBySymbolTest",
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
