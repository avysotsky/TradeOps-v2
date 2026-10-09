using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class TradingSignalOutcomeHistoryPostgresTests
{
    [Fact]
    public async Task Repository_PersistsReceivedAndTerminalOutcomeEventsAtomicallyWithProjection()
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

        var databaseName = $"tradeops_signal_history_{Guid.NewGuid():N}";
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

            var signalRepository = new EfTradingSignalRepository(dbContext);
            var historyRepository = new EfTradingSignalOutcomeHistoryRepository(dbContext);

            var rejectedSignal = NewSignal(
                Guid.Parse("11111111-1111-4111-8111-111111111111"),
                "BTCUSDT");

            Assert.True(await signalRepository.TryAddAsync(rejectedSignal));

            var initialHistory = await historyRepository.GetBySignalIdAsync(rejectedSignal.Id);
            var received = Assert.Single(initialHistory);
            Assert.Null(received.PreviousOutcome);
            Assert.Equal(SignalOutcome.Received, received.Outcome);
            Assert.Empty(received.RiskRejectionReasons);
            Assert.Null(received.OrderId);
            Assert.Null(received.ClientOrderId);

            rejectedSignal.Outcome = SignalOutcome.Rejected;
            rejectedSignal.RiskRejectionReasons = ["Emergency stop is active."];
            await signalRepository.UpdateAsync(rejectedSignal);

            var rejectedHistory = (await historyRepository.GetBySignalIdAsync(rejectedSignal.Id)).ToArray();
            Assert.Equal(2, rejectedHistory.Length);
            Assert.Equal(SignalOutcome.Received, rejectedHistory[1].PreviousOutcome);
            Assert.Equal(SignalOutcome.Rejected, rejectedHistory[1].Outcome);
            Assert.Equal(rejectedSignal.RiskRejectionReasons, rejectedHistory[1].RiskRejectionReasons);
            Assert.True(rejectedHistory[0].OccurredAt <= rejectedHistory[1].OccurredAt);

            await signalRepository.UpdateAsync(rejectedSignal);
            Assert.Equal(2, (await historyRepository.GetBySignalIdAsync(rejectedSignal.Id)).Count);

            var acceptedOrder = new Order
            {
                Id = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
                ClientOrderId = "signal-history-accepted-order",
                ExchangeOrderId = "exchange-order",
                Symbol = "ETHUSDT",
                Side = OrderSide.Buy,
                OrderType = OrderType.Market,
                RequestedQuantity = 1m,
                Status = OrderStatus.Accepted,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            dbContext.Orders.Add(acceptedOrder);
            await dbContext.SaveChangesAsync();

            var acceptedSignal = NewSignal(
                Guid.Parse("22222222-2222-4222-8222-222222222222"),
                "ETHUSDT");
            Assert.True(await signalRepository.TryAddAsync(acceptedSignal));

            acceptedSignal.Outcome = SignalOutcome.Accepted;
            acceptedSignal.OrderId = acceptedOrder.Id;
            acceptedSignal.ClientOrderId = acceptedOrder.ClientOrderId;
            await signalRepository.UpdateAsync(acceptedSignal);

            var acceptedHistory = (await historyRepository.GetBySignalIdAsync(acceptedSignal.Id)).ToArray();
            Assert.Equal(2, acceptedHistory.Length);
            Assert.Equal(SignalOutcome.Received, acceptedHistory[1].PreviousOutcome);
            Assert.Equal(SignalOutcome.Accepted, acceptedHistory[1].Outcome);
            Assert.Equal(acceptedOrder.Id, acceptedHistory[1].OrderId);
            Assert.Equal(acceptedOrder.ClientOrderId, acceptedHistory[1].ClientOrderId);

            var projections = await dbContext.TradingSignals
                .AsNoTracking()
                .OrderBy(item => item.Id)
                .ToArrayAsync();
            Assert.Equal(2, projections.Length);
            Assert.Contains(projections, item => item.Id == rejectedSignal.Id && item.Outcome == SignalOutcome.Rejected);
            Assert.Contains(projections, item => item.Id == acceptedSignal.Id && item.Outcome == SignalOutcome.Accepted);
            Assert.Equal(4, await dbContext.TradingSignalOutcomeEvents.LongCountAsync());
        }
        finally
        {
            await using var dropDatabase = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
                adminConnection);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    private static TradingSignal NewSignal(Guid id, string symbol) =>
        new()
        {
            Id = id,
            Symbol = symbol,
            Side = OrderSide.Buy,
            SignalType = "External",
            RequestedQuantity = 1m,
            CreatedAt = DateTimeOffset.UtcNow,
            Source = "SignalOutcomeHistoryTest"
        };
}
