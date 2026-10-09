using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class ExecutionMetricsSeriesPostgresTests
{
    [Fact]
    public async Task GetSeriesAsync_AggregatesInPostgresAndPreservesSeriesSemantics()
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

        var databaseName = $"tradeops_metrics_{Guid.NewGuid():N}";
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
            var btcOrderId = Guid.NewGuid();
            var ethOrderId = Guid.NewGuid();

            dbContext.Orders.AddRange(
                new Order
                {
                    Id = btcOrderId,
                    ClientOrderId = "metrics-btc-order",
                    Symbol = "BTCUSDT",
                    Side = OrderSide.Buy,
                    OrderType = OrderType.Market,
                    RequestedQuantity = 1m,
                    Status = OrderStatus.Accepted,
                    CreatedAt = fromInclusive,
                    UpdatedAt = fromInclusive
                },
                new Order
                {
                    Id = ethOrderId,
                    ClientOrderId = "metrics-eth-order",
                    Symbol = "ETHUSDT",
                    Side = OrderSide.Sell,
                    OrderType = OrderType.Market,
                    RequestedQuantity = 1m,
                    Status = OrderStatus.Accepted,
                    CreatedAt = fromInclusive,
                    UpdatedAt = fromInclusive
                });
            await dbContext.SaveChangesAsync();

            dbContext.TradingSignals.AddRange(
                NewSignal("BTCUSDT", SignalOutcome.Accepted, fromInclusive.AddSeconds(30)),
                NewSignal("BTCUSDT", SignalOutcome.Rejected, new DateTimeOffset(2026, 10, 1, 10, 7, 0, TimeSpan.Zero)),
                NewSignal("BTCUSDT", SignalOutcome.Received, new DateTimeOffset(2026, 10, 1, 10, 7, 30, TimeSpan.Zero)),
                NewSignal("ETHUSDT", SignalOutcome.Accepted, new DateTimeOffset(2026, 10, 1, 10, 7, 45, TimeSpan.Zero)),
                NewSignal("BTCUSDT", SignalOutcome.Accepted, toExclusive));

            dbContext.OrderLifecycleEvents.AddRange(
                NewLifecycle(btcOrderId, "metrics-btc-order", OrderStatus.Created, new DateTimeOffset(2026, 10, 1, 10, 3, 0, TimeSpan.Zero)),
                NewLifecycle(btcOrderId, "metrics-btc-order", OrderStatus.Submitted, new DateTimeOffset(2026, 10, 1, 10, 4, 0, TimeSpan.Zero)),
                NewLifecycle(btcOrderId, "metrics-btc-order", OrderStatus.Accepted, new DateTimeOffset(2026, 10, 1, 10, 6, 0, TimeSpan.Zero)),
                NewLifecycle(ethOrderId, "metrics-eth-order", OrderStatus.Accepted, new DateTimeOffset(2026, 10, 1, 10, 6, 30, TimeSpan.Zero)));

            dbContext.Fills.AddRange(
                NewFill(btcOrderId, "metrics-btc-fill", new DateTimeOffset(2026, 10, 1, 10, 8, 0, TimeSpan.Zero)),
                NewFill(ethOrderId, "metrics-eth-fill", new DateTimeOffset(2026, 10, 1, 10, 8, 30, TimeSpan.Zero)));

            await dbContext.SaveChangesAsync();

            var repository = new EfExecutionMetricsRepository(dbContext, new RiskSettings());
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
            Assert.Equal(1, first.Signals.Received);
            Assert.Equal(1, first.Signals.AcceptedCurrentOutcome);
            Assert.Equal(0, first.Signals.RejectedCurrentOutcome);
            Assert.Equal(0, first.Signals.PendingCurrentOutcome);
            Assert.Equal(2, first.OrderLifecycle.Events);
            Assert.Equal(1, first.OrderLifecycle.OrdersTouched);
            Assert.Equal(1, first.OrderLifecycle.Created);
            Assert.Equal(1, first.OrderLifecycle.Submitted);
            Assert.Equal(0, first.FillsReceived);

            var second = snapshot.Buckets[1];
            Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 5, 0, TimeSpan.Zero), second.FromInclusive);
            Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 10, 0, TimeSpan.Zero), second.ToExclusive);
            Assert.Equal(2, second.Signals.Received);
            Assert.Equal(0, second.Signals.AcceptedCurrentOutcome);
            Assert.Equal(1, second.Signals.RejectedCurrentOutcome);
            Assert.Equal(1, second.Signals.PendingCurrentOutcome);
            Assert.Equal(1, second.OrderLifecycle.Events);
            Assert.Equal(1, second.OrderLifecycle.OrdersTouched);
            Assert.Equal(1, second.OrderLifecycle.Accepted);
            Assert.Equal(1, second.FillsReceived);

            var third = snapshot.Buckets[2];
            Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 10, 0, TimeSpan.Zero), third.FromInclusive);
            Assert.Equal(toExclusive, third.ToExclusive);
            Assert.Equal(0, third.Signals.Received);
            Assert.Equal(0, third.OrderLifecycle.Events);
            Assert.Equal(0, third.OrderLifecycle.OrdersTouched);
            Assert.Equal(0, third.FillsReceived);
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
        SignalOutcome outcome,
        DateTimeOffset createdAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            Symbol = symbol,
            Side = OrderSide.Buy,
            RequestedQuantity = 1m,
            CreatedAt = createdAt,
            Outcome = outcome
        };

    private static OrderLifecycleEvent NewLifecycle(
        Guid orderId,
        string clientOrderId,
        OrderStatus status,
        DateTimeOffset occurredAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            ClientOrderId = clientOrderId,
            Status = status,
            Source = "MetricsSeriesTest",
            OccurredAt = occurredAt
        };

    private static Fill NewFill(
        Guid orderId,
        string exchangeFillId,
        DateTimeOffset filledAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            ExchangeFillId = exchangeFillId,
            Quantity = 1m,
            Price = 100m,
            FilledAt = filledAt
        };
}
