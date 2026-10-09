using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Persistence;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class TradingViewDeliveryHealthApiIntegrationTests
{
    [Fact]
    public async Task HealthEndpoint_ReturnsPersistedWorkerEvaluation()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable(
            "TRADEOPS_TEST_POSTGRES_ADMIN");
        var isGitHubActions = string.Equals(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(adminConnectionString)
            && !isGitHubActions)
        {
            return;
        }

        adminConnectionString ??=
            "Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres";

        var databaseName =
            $"tradeops_tv_health_{Guid.NewGuid():N}";

        var adminBuilder = new NpgsqlConnectionStringBuilder(
            adminConnectionString)
        {
            Database = "postgres",
            Pooling = false
        };

        var testBuilder = new NpgsqlConnectionStringBuilder(
            adminBuilder.ConnectionString)
        {
            Database = databaseName,
            Pooling = false
        };

        await using var adminConnection = new NpgsqlConnection(
            adminBuilder.ConnectionString);
        await adminConnection.OpenAsync();

        await using (var createDatabase = new NpgsqlCommand(
                         $"CREATE DATABASE \"{databaseName}\"",
                         adminConnection))
        {
            await createDatabase.ExecuteNonQueryAsync();
        }

        try
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseSetting(
                        "ConnectionStrings:TradeOpsDb",
                        testBuilder.ConnectionString);
                    builder.UseSetting(
                        "Exchange:Provider",
                        "Mock");
                    builder.UseSetting(
                        "Telegram:Enabled",
                        "false");
                });

            using var client = factory.CreateClient();

            using (var missing = await client.GetAsync(
                       "/api/integrations/tradingview/operations/health"))
            {
                Assert.Equal(
                    HttpStatusCode.NotFound,
                    missing.StatusCode);
            }

            var now = DateTimeOffset.UtcNow;

            await using (var scope =
                         factory.Services.CreateAsyncScope())
            {
                var dbContext = scope.ServiceProvider
                    .GetRequiredService<TradeOpsDbContext>();

                dbContext.TradingViewDeliveryHealthStates.Add(
                    new TradingViewDeliveryHealthState
                    {
                        Id = TradingViewDeliveryHealthState.SingletonId,
                        Status =
                            TradingViewDeliveryHealthStatus.Degraded,
                        Reason = "Average adapter latency exceeded threshold.",
                        UpdatedAt = now,
                        WindowFrom = now.AddMinutes(-15),
                        WindowTo = now,
                        Total = 12,
                        Failed = 0,
                        Conflict = 1,
                        RiskRejected = 0,
                        AverageLatencyMilliseconds = 1250,
                        LatestDeliveryAt = now.AddSeconds(-10),
                        LatestSuccessfulAt = now.AddSeconds(-10)
                    });

                await dbContext.SaveChangesAsync();
            }

            using var response = await client.GetAsync(
                "/api/integrations/tradingview/operations/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var health = await response.Content
                .ReadFromJsonAsync<HealthResponse>();

            Assert.NotNull(health);
            Assert.Equal("Degraded", health.Status);
            Assert.Equal(12, health.Total);
            Assert.NotNull(health.AverageLatencyMilliseconds);
            Assert.Equal(
                1250d,
                health.AverageLatencyMilliseconds.Value);
            Assert.NotNull(health.LatestSuccessfulAt);
        }
        finally
        {
            await using var dropDatabase = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
                adminConnection);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    private sealed record HealthResponse(
        string Status,
        string Reason,
        DateTimeOffset UpdatedAt,
        DateTimeOffset WindowFrom,
        DateTimeOffset WindowTo,
        int Total,
        int Failed,
        int Conflict,
        int RiskRejected,
        double? AverageLatencyMilliseconds,
        DateTimeOffset? LatestDeliveryAt,
        DateTimeOffset? LatestSuccessfulAt);
}
