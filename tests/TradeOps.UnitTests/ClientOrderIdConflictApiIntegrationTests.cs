using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TradeOps.Application.Services;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Persistence;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class ClientOrderIdConflictApiIntegrationTests
{
    [Fact]
    public async Task Post_ConflictingDeterministicLocalOrder_Returns409AndDoesNotPlaceOnExchange()
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

        var databaseName = $"tradeops_client_order_identity_{Guid.NewGuid():N}";
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
            var generator = new ClientOrderIdGenerator();

            var conflictingSignalId = Guid.Parse("81818181-8181-4181-8181-818181818181");
            var conflictingOrder = NewOrder(
                Guid.Parse("82828282-8282-4282-8282-828282828282"),
                generator.Generate(conflictingSignalId),
                0.002m);

            await SeedOrderAsync(factory.Services, conflictingOrder);

            using (var conflictResponse = await PostSignalAsync(
                       client,
                       conflictingSignalId,
                       0.001m))
            {
                Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);

                using var document = JsonDocument.Parse(await conflictResponse.Content.ReadAsStringAsync());
                Assert.Equal(conflictingSignalId, document.RootElement.GetProperty("signalId").GetGuid());
                Assert.Contains(
                    "RequestedQuantity",
                    document.RootElement
                        .GetProperty("conflictingFields")
                        .EnumerateArray()
                        .Select(item => item.GetString()));
            }

            using (var retryResponse = await PostSignalAsync(
                       client,
                       conflictingSignalId,
                       0.001m))
            {
                Assert.Equal(HttpStatusCode.Conflict, retryResponse.StatusCode);
            }

            using (var exchangeOrders = await client.GetAsync("/api/orders"))
            {
                Assert.Equal(HttpStatusCode.OK, exchangeOrders.StatusCode);
                using var document = JsonDocument.Parse(await exchangeOrders.Content.ReadAsStringAsync());
                Assert.Equal(0, document.RootElement.GetArrayLength());
            }

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<TradeOpsDbContext>();
                var signal = await dbContext.TradingSignals.SingleAsync(item => item.Id == conflictingSignalId);

                Assert.Equal(SignalOutcome.Received, signal.Outcome);
                Assert.Null(signal.OrderId);
                Assert.Null(signal.ClientOrderId);
                Assert.Equal("ClientOrderIdConflict", signal.ExecutionIssueCode);
                Assert.NotNull(signal.ExecutionIssueMessage);
                Assert.NotNull(signal.ExecutionIssueAt);
                Assert.Equal(1, await dbContext.TradingSignalOutcomeEvents.CountAsync(
                    item => item.TradingSignalId == conflictingSignalId));
                Assert.Equal(1, await dbContext.Orders.CountAsync());
            }

            using (var auditResponse = await client.GetAsync($"/api/signals/{conflictingSignalId}"))
            {
                Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
                using var document = JsonDocument.Parse(await auditResponse.Content.ReadAsStringAsync());
                Assert.Equal("Received", document.RootElement.GetProperty("outcome").GetString());
                Assert.Equal("ClientOrderIdConflict", document.RootElement.GetProperty("executionIssueCode").GetString());
                Assert.False(string.IsNullOrWhiteSpace(
                    document.RootElement.GetProperty("executionIssueMessage").GetString()));
                Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("executionIssueAt").ValueKind);
            }

            await FixOrderQuantityAsync(
                factory.Services,
                conflictingOrder.Id,
                0.001m);

            using (var recoveredResponse = await PostSignalAsync(
                       client,
                       conflictingSignalId,
                       0.001m))
            {
                Assert.Equal(HttpStatusCode.OK, recoveredResponse.StatusCode);
            }

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<TradeOpsDbContext>();
                var signal = await dbContext.TradingSignals.SingleAsync(item => item.Id == conflictingSignalId);

                Assert.Equal(SignalOutcome.Accepted, signal.Outcome);
                Assert.Equal(conflictingOrder.Id, signal.OrderId);
                Assert.Equal(conflictingOrder.ClientOrderId, signal.ClientOrderId);
                Assert.Null(signal.ExecutionIssueCode);
                Assert.Null(signal.ExecutionIssueMessage);
                Assert.Null(signal.ExecutionIssueAt);
                Assert.Equal(2, await dbContext.TradingSignalOutcomeEvents.CountAsync(
                    item => item.TradingSignalId == conflictingSignalId));
            }

            var matchingSignalId = Guid.Parse("83838383-8383-4383-8383-838383838383");
            var matchingOrder = NewOrder(
                Guid.Parse("84848484-8484-4484-8484-848484848484"),
                generator.Generate(matchingSignalId),
                0.001m);

            await SeedOrderAsync(factory.Services, matchingOrder);

            using (var matchingResponse = await PostSignalAsync(
                       client,
                       matchingSignalId,
                       0.001m))
            {
                Assert.Equal(HttpStatusCode.OK, matchingResponse.StatusCode);

                using var document = JsonDocument.Parse(await matchingResponse.Content.ReadAsStringAsync());
                Assert.Equal(
                    matchingOrder.ClientOrderId,
                    document.RootElement.GetProperty("order").GetProperty("clientOrderId").GetString());
            }

            using (var exchangeOrders = await client.GetAsync("/api/orders"))
            {
                Assert.Equal(HttpStatusCode.OK, exchangeOrders.StatusCode);
                using var document = JsonDocument.Parse(await exchangeOrders.Content.ReadAsStringAsync());
                Assert.Equal(0, document.RootElement.GetArrayLength());
            }

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<TradeOpsDbContext>();
                var signal = await dbContext.TradingSignals.SingleAsync(item => item.Id == matchingSignalId);

                Assert.Equal(SignalOutcome.Accepted, signal.Outcome);
                Assert.Equal(matchingOrder.Id, signal.OrderId);
                Assert.Equal(matchingOrder.ClientOrderId, signal.ClientOrderId);
                Assert.Null(signal.ExecutionIssueCode);
                Assert.Null(signal.ExecutionIssueMessage);
                Assert.Null(signal.ExecutionIssueAt);
                Assert.Equal(2, await dbContext.TradingSignalOutcomeEvents.CountAsync(
                    item => item.TradingSignalId == matchingSignalId));
                Assert.Equal(2, await dbContext.Orders.CountAsync());
            }
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

    private static Task<HttpResponseMessage> PostSignalAsync(
        HttpClient client,
        Guid signalId,
        decimal quantity) =>
        client.PostAsJsonAsync(
            "/api/signals",
            new
            {
                symbol = "BTCUSDT",
                side = "Buy",
                quantity,
                source = "ClientOrderIdentityApiTest",
                signalId
            });

    private static async Task FixOrderQuantityAsync(
        IServiceProvider services,
        Guid orderId,
        decimal requestedQuantity)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradeOpsDbContext>();
        var order = await dbContext.Orders.SingleAsync(item => item.Id == orderId);
        order.RequestedQuantity = requestedQuantity;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync();
    }

    private static async Task SeedOrderAsync(
        IServiceProvider services,
        Order order)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradeOpsDbContext>();
        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync();
    }

    private static Order NewOrder(
        Guid id,
        string clientOrderId,
        decimal requestedQuantity) =>
        new()
        {
            Id = id,
            ExchangeOrderId = $"seeded-{id:N}",
            ClientOrderId = clientOrderId,
            Symbol = "BTCUSDT",
            Side = OrderSide.Buy,
            OrderType = OrderType.Market,
            RequestedQuantity = requestedQuantity,
            FilledQuantity = 0m,
            Status = OrderStatus.Accepted,
            CreatedAt = DateTimeOffset.Parse("2026-10-02T09:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-10-02T09:00:00Z")
        };
}
