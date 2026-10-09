using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Persistence;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class LocalOrderAuditApiIntegrationTests
{
    [Fact]
    public async Task GetLocalOrders_FiltersNormalizesAndRejectsInvalidAuditQueriesAgainstPostgres()
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

        var databaseName = $"tradeops_local_order_audit_api_{Guid.NewGuid():N}";
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

        WebApplicationFactory<Program>? factory = null;
        HttpClient? client = null;

        try
        {
            factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("ConnectionStrings:TradeOpsDb", testBuilder.ConnectionString);
                    builder.UseSetting("Exchange:Provider", "Mock");
                    builder.UseSetting("Telegram:Enabled", "false");
                });

            client = factory.CreateClient();

            var fromUtc = DateTimeOffset.Parse("2026-10-01T08:00:00Z");
            var toUtc = DateTimeOffset.Parse("2026-10-01T09:00:00Z");
            var btcFilledId = Guid.Parse("11111111-1111-4111-8111-111111111111");
            var btcCancelledId = Guid.Parse("22222222-2222-4222-8222-222222222222");
            var ethRejectedId = Guid.Parse("33333333-3333-4333-8333-333333333333");

            await SeedOrdersAsync(
                factory.Services,
                NewOrder(btcFilledId, "audit-client-1", "BTCUSDT", OrderStatus.Filled, fromUtc.AddMinutes(15)),
                NewOrder(btcCancelledId, "audit-client-2", "BTCUSDT", OrderStatus.Cancelled, fromUtc.AddMinutes(30)),
                NewOrder(ethRejectedId, "audit-client-3", "ETHUSDT", OrderStatus.Rejected, fromUtc.AddMinutes(45)),
                NewOrder(
                    Guid.Parse("44444444-4444-4444-8444-444444444444"),
                    "audit-client-4",
                    "BTCUSDT",
                    OrderStatus.Filled,
                    toUtc));

            var fromLocal = DateTimeOffset.Parse("2026-10-01T10:00:00+02:00");
            var toLocal = DateTimeOffset.Parse("2026-10-01T11:00:00+02:00");

            using (var filteredResponse = await client.GetAsync(
                       BuildUri(fromLocal, toLocal) +
                       "&symbol=%20btcusdt%20&status=Filled&limit=10"))
            {
                Assert.Equal(HttpStatusCode.OK, filteredResponse.StatusCode);

                using var document = JsonDocument.Parse(await filteredResponse.Content.ReadAsStringAsync());
                var items = document.RootElement;
                Assert.Equal(1, items.GetArrayLength());
                Assert.Equal(btcFilledId, items[0].GetProperty("id").GetGuid());
                Assert.Equal("BTCUSDT", items[0].GetProperty("symbol").GetString());
                Assert.Equal("Filled", items[0].GetProperty("status").GetString());
            }

            using (var windowResponse = await client.GetAsync(BuildUri(fromLocal, toLocal) + "&limit=2"))
            {
                Assert.Equal(HttpStatusCode.OK, windowResponse.StatusCode);

                using var document = JsonDocument.Parse(await windowResponse.Content.ReadAsStringAsync());
                var items = document.RootElement;
                Assert.Equal(2, items.GetArrayLength());
                Assert.Equal(ethRejectedId, items[0].GetProperty("id").GetGuid());
                Assert.Equal(btcCancelledId, items[1].GetProperty("id").GetGuid());
            }

            await AssertBadRequestAsync(client, "/api/orders/local?limit=0");
            await AssertBadRequestAsync(client, "/api/orders/local?limit=201");
            await AssertBadRequestAsync(client, "/api/orders/local?symbol=" + new string('A', 51));
            await AssertBadRequestAsync(client, "/api/orders/local?status=NotARealStatus");
            await AssertBadRequestAsync(
                client,
                "/api/orders/local?from=2026-10-01T10%3A00%3A00Z&to=2026-10-01T10%3A00%3A00Z");
        }
        finally
        {
            client?.Dispose();
            factory?.Dispose();

            await using var dropDatabase = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
                adminConnection);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    private static async Task SeedOrdersAsync(
        IServiceProvider services,
        params Order[] orders)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradeOpsDbContext>();
        dbContext.Orders.AddRange(orders);
        await dbContext.SaveChangesAsync();
    }

    private static async Task AssertBadRequestAsync(HttpClient client, string uri)
    {
        using var response = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string BuildUri(DateTimeOffset from, DateTimeOffset to) =>
        "/api/orders/local" +
        $"?from={Uri.EscapeDataString(from.ToString("O"))}" +
        $"&to={Uri.EscapeDataString(to.ToString("O"))}";

    private static Order NewOrder(
        Guid id,
        string clientOrderId,
        string symbol,
        OrderStatus status,
        DateTimeOffset createdAt) =>
        new()
        {
            Id = id,
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
