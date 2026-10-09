using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TradeOps.Infrastructure.Persistence;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class SignalRequestValidationApiIntegrationTests
{
    [Fact]
    public async Task Post_InvalidExecutionInstructions_Returns400BeforePersistence()
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

        var databaseName = $"tradeops_signal_request_validation_{Guid.NewGuid():N}";
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

            var invalidPayloads = new object[]
            {
                new { symbol = "   ", side = "Buy", quantity = 0.001m, signalId = Guid.NewGuid() },
                new { symbol = "BTCUSDT", side = 999, quantity = 0.001m, signalId = Guid.NewGuid() },
                new { symbol = "BTCUSDT", side = "Buy", quantity = 0m, signalId = Guid.NewGuid() },
                new { symbol = "BTCUSDT", side = "Buy", quantity = 0.0000000000001m, signalId = Guid.NewGuid() },
                new { symbol = "BTCUSDT", side = "Buy", quantity = 0.001m, riskPercent = 101m, signalId = Guid.NewGuid() },
                new { symbol = "BTCUSDT", side = "Buy", quantity = 0.001m, stopLoss = 0m, signalId = Guid.NewGuid() },
                new { symbol = "BTCUSDT", side = "Buy", quantity = 0.001m, takeProfit = 1.1234567890123m, signalId = Guid.NewGuid() },
                new { symbol = "BTCUSDT", side = "Buy", quantity = 0.001m, source = new string('s', 101), signalId = Guid.NewGuid() },
                new { symbol = "BTCUSDT", side = "Buy", quantity = 0.001m, signalId = Guid.Empty }
            };

            foreach (var payload in invalidPayloads)
            {
                using var response = await client.PostAsJsonAsync("/api/signals", payload);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<TradeOpsDbContext>();
                Assert.Equal(0, await dbContext.TradingSignals.CountAsync());
                Assert.Equal(0, await dbContext.TradingSignalOutcomeEvents.CountAsync());
                Assert.Equal(0, await dbContext.Orders.CountAsync());
            }

            using var riskRejected = await client.PostAsJsonAsync(
                "/api/signals",
                new
                {
                    symbol = "BTCUSDT",
                    side = "Buy",
                    quantity = 0.11m,
                    signalId = Guid.NewGuid()
                });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, riskRejected.StatusCode);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<TradeOpsDbContext>();
                Assert.Equal(1, await dbContext.TradingSignals.CountAsync());
                Assert.Equal(2, await dbContext.TradingSignalOutcomeEvents.CountAsync());
                Assert.Equal(0, await dbContext.Orders.CountAsync());
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
}
