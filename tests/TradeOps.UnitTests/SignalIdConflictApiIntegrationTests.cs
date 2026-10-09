using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TradeOps.Infrastructure.Persistence;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class SignalIdConflictApiIntegrationTests
{
    [Fact]
    public async Task Post_SameSignalIdWithDifferentExecutionPayload_ReturnsConflictWithoutSecondOrder()
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

        var databaseName = $"tradeops_signal_id_conflict_{Guid.NewGuid():N}";
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
            var signalId = Guid.Parse("88888888-8888-4888-8888-888888888888");

            using var firstResponse = await client.PostAsJsonAsync(
                "/api/signals",
                new
                {
                    symbol = "BTCUSDT",
                    side = "Buy",
                    quantity = 0.001m,
                    source = "original",
                    signalId
                });
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

            using var firstDocument = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
            var firstClientOrderId = firstDocument.RootElement
                .GetProperty("order")
                .GetProperty("clientOrderId")
                .GetString();
            Assert.False(string.IsNullOrWhiteSpace(firstClientOrderId));

            using var exactRetryResponse = await client.PostAsJsonAsync(
                "/api/signals",
                new
                {
                    symbol = " btcusdt ",
                    side = "Buy",
                    quantity = 0.001m,
                    source = "retry-metadata",
                    signalId
                });
            Assert.Equal(HttpStatusCode.OK, exactRetryResponse.StatusCode);

            using var retryDocument = JsonDocument.Parse(await exactRetryResponse.Content.ReadAsStringAsync());
            Assert.Equal(
                firstClientOrderId,
                retryDocument.RootElement.GetProperty("order").GetProperty("clientOrderId").GetString());

            using var conflictResponse = await client.PostAsJsonAsync(
                "/api/signals",
                new
                {
                    symbol = "BTCUSDT",
                    side = "Buy",
                    quantity = 0.002m,
                    source = "conflicting-retry",
                    signalId
                });
            Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);

            using var conflictDocument = JsonDocument.Parse(await conflictResponse.Content.ReadAsStringAsync());
            Assert.Equal(signalId, conflictDocument.RootElement.GetProperty("signalId").GetGuid());
            Assert.Contains(
                "RequestedQuantity",
                conflictDocument.RootElement
                    .GetProperty("conflictingFields")
                    .EnumerateArray()
                    .Select(item => item.GetString()));

            await using var scope = factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TradeOpsDbContext>();

            Assert.Equal(1, await dbContext.TradingSignals.CountAsync());
            Assert.Equal(1, await dbContext.Orders.CountAsync());
            Assert.Equal(2, await dbContext.TradingSignalOutcomeEvents.CountAsync());
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
}
