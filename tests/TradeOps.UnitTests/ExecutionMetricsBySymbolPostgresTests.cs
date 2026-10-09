using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class ExecutionMetricsBySymbolPostgresTests
{
    [Fact]
    public async Task GetAsync_AggregatesAllFactFamiliesAndEnforcesBoundedResult()
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

        var databaseName = $"tradeops_metrics_symbol_{Guid.NewGuid():N}";
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

            var fromInclusive = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
            var toExclusive = new DateTimeOffset(2026, 10, 1, 11, 0, 0, TimeSpan.Zero);
            var btcOrderId = Guid.NewGuid();
            var ethOrderId = Guid.NewGuid();
            var adaOrderId = Guid.NewGuid();

            dbContext.Orders.AddRange(
                NewOrder(btcOrderId, "metrics-symbol-btc", "BTCUSDT", fromInclusive),
                NewOrder(ethOrderId, "metrics-symbol-eth", "ETHUSDT", fromInclusive),
                NewOrder(adaOrderId, "metrics-symbol-ada", "ADAUSDT", fromInclusive));
            await dbContext.SaveChangesAsync();

            dbContext.TradingSignals.AddRange(
                NewSignal("BTCUSDT", SignalOutcome.Accepted, fromInclusive.AddMinutes(5)),
                NewSignal("BTCUSDT", SignalOutcome.Rejected, fromInclusive.AddMinutes(6)),
                NewSignal("ETHUSDT", SignalOutcome.Received, fromInclusive.AddMinutes(10)),
                NewSignal("XRPUSDT", SignalOutcome.Accepted, fromInclusive.AddMinutes(15)),
                NewSignal("BTCUSDT", SignalOutcome.Accepted, toExclusive));

            dbContext.OrderLifecycleEvents.AddRange(
                NewLifecycle(btcOrderId, "metrics-symbol-btc", OrderStatus.Created, fromInclusive.AddMinutes(20)),
                NewLifecycle(btcOrderId, "metrics-symbol-btc", OrderStatus.Submitted, fromInclusive.AddMinutes(21)),
                NewLifecycle(ethOrderId, "metrics-symbol-eth", OrderStatus.Accepted, fromInclusive.AddMinutes(22)));

            dbContext.Fills.AddRange(
                NewFill(btcOrderId, "metrics-symbol-btc-fill", fromInclusive.AddMinutes(30)),
                NewFill(adaOrderId, "metrics-symbol-ada-fill", fromInclusive.AddMinutes(31)));

            await dbContext.SaveChangesAsync();

            var repository = new EfExecutionMetricsBySymbolRepository(dbContext);
            var full = await repository.GetAsync(fromInclusive, toExclusive, 10);

            Assert.Equal(fromInclusive, full.FromInclusive);
            Assert.Equal(toExclusive, full.ToExclusive);
            Assert.Equal(10, full.Limit);
            Assert.False(full.IsTruncated);
            Assert.Equal(new[] { "BTCUSDT", "ETHUSDT", "ADAUSDT", "XRPUSDT" },
                full.Symbols.Select(item => item.Symbol).ToArray());

            var btc = full.Symbols[0];
            Assert.Equal(2, btc.Signals.Received);
            Assert.Equal(1, btc.Signals.AcceptedCurrentOutcome);
            Assert.Equal(1, btc.Signals.RejectedCurrentOutcome);
            Assert.Equal(0, btc.Signals.PendingCurrentOutcome);
            Assert.Equal(2, btc.OrderLifecycle.Events);
            Assert.Equal(1, btc.OrderLifecycle.OrdersTouched);
            Assert.Equal(1, btc.OrderLifecycle.Created);
            Assert.Equal(1, btc.OrderLifecycle.Submitted);
            Assert.Equal(1, btc.FillsReceived);

            var eth = full.Symbols[1];
            Assert.Equal(1, eth.Signals.Received);
            Assert.Equal(1, eth.Signals.PendingCurrentOutcome);
            Assert.Equal(1, eth.OrderLifecycle.Events);
            Assert.Equal(1, eth.OrderLifecycle.OrdersTouched);
            Assert.Equal(1, eth.OrderLifecycle.Accepted);
            Assert.Equal(0, eth.FillsReceived);

            var ada = full.Symbols[2];
            Assert.Equal(0, ada.Signals.Received);
            Assert.Equal(0, ada.OrderLifecycle.Events);
            Assert.Equal(1, ada.FillsReceived);

            var xrp = full.Symbols[3];
            Assert.Equal(1, xrp.Signals.Received);
            Assert.Equal(1, xrp.Signals.AcceptedCurrentOutcome);
            Assert.Equal(0, xrp.OrderLifecycle.Events);
            Assert.Equal(0, xrp.FillsReceived);

            var bounded = await repository.GetAsync(fromInclusive, toExclusive, 2);
            Assert.Equal(2, bounded.Limit);
            Assert.True(bounded.IsTruncated);
            Assert.Equal(new[] { "BTCUSDT", "ETHUSDT" },
                bounded.Symbols.Select(item => item.Symbol).ToArray());
        }
        finally
        {
            await using var dropDatabase = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
                adminConnection);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    private static Order NewOrder(
        Guid id,
        string clientOrderId,
        string symbol,
        DateTimeOffset createdAt) =>
        new()
        {
            Id = id,
            ClientOrderId = clientOrderId,
            Symbol = symbol,
            Side = OrderSide.Buy,
            OrderType = OrderType.Market,
            RequestedQuantity = 1m,
            Status = OrderStatus.Filled,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

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
            Source = "MetricsBySymbolTest",
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
