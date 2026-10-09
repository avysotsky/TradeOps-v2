using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class LocalOrderAuditPostgresTests
{
    [Fact]
    public async Task GetAsync_FiltersByCreationTimeSymbolStatusAndLimitDeterministically()
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

        var databaseName = $"tradeops_local_order_audit_{Guid.NewGuid():N}";
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

            var atFrom = NewOrder(
                "11111111-1111-4111-8111-111111111111",
                "client-1",
                "BTCUSDT",
                OrderStatus.Accepted,
                from);
            var btcFilled = NewOrder(
                "22222222-2222-4222-8222-222222222222",
                "client-2",
                "BTCUSDT",
                OrderStatus.Filled,
                from.AddMinutes(10));
            var ethRejected = NewOrder(
                "33333333-3333-4333-8333-333333333333",
                "client-3",
                "ETHUSDT",
                OrderStatus.Rejected,
                from.AddMinutes(20));
            var btcCancelled = NewOrder(
                "44444444-4444-4444-8444-444444444444",
                "client-4",
                "BTCUSDT",
                OrderStatus.Cancelled,
                from.AddMinutes(30));
            var before = NewOrder(
                "55555555-5555-4555-8555-555555555555",
                "client-5",
                "BTCUSDT",
                OrderStatus.Cancelled,
                from.AddTicks(-1));
            var atTo = NewOrder(
                "66666666-6666-4666-8666-666666666666",
                "client-6",
                "BTCUSDT",
                OrderStatus.Filled,
                to);

            dbContext.Orders.AddRange(
                atFrom,
                btcFilled,
                ethRejected,
                btcCancelled,
                before,
                atTo);
            await dbContext.SaveChangesAsync();

            var repository = new EfLocalOrderAuditRepository(dbContext);

            var window = await repository.GetAsync(
                symbol: null,
                status: null,
                fromInclusive: from,
                toExclusive: to,
                limit: 50);

            Assert.Equal(
                [btcCancelled.Id, ethRejected.Id, btcFilled.Id, atFrom.Id],
                window.Select(item => item.Id).ToArray());

            var btcCancelledOnly = await repository.GetAsync(
                symbol: " btcusdt ",
                status: OrderStatus.Cancelled,
                fromInclusive: from,
                toExclusive: to,
                limit: 50);

            var filtered = Assert.Single(btcCancelledOnly);
            Assert.Equal(btcCancelled.Id, filtered.Id);

            var limited = await repository.GetAsync(
                symbol: null,
                status: null,
                fromInclusive: from,
                toExclusive: to,
                limit: 2);

            Assert.Equal(
                [btcCancelled.Id, ethRejected.Id],
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

    private static Order NewOrder(
        string id,
        string clientOrderId,
        string symbol,
        OrderStatus status,
        DateTimeOffset createdAt) =>
        new()
        {
            Id = Guid.Parse(id),
            ClientOrderId = clientOrderId,
            Symbol = symbol,
            Side = OrderSide.Buy,
            OrderType = OrderType.Market,
            RequestedQuantity = 1m,
            FilledQuantity = status == OrderStatus.Filled ? 1m : 0m,
            Status = status,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
}
