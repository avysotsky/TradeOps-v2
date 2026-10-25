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

public sealed class SignalIngressReplayProtectionApiIntegrationTests
{
    private const string ApiKeyHeader = "X-TradeOps-Api-Key";
    private const string ApiKey = "replay-integration-secret";
    private const string TimestampHeader = "X-TradeOps-Timestamp";
    private const string RequestIdHeader = "X-TradeOps-Request-Id";

    [Fact]
    public async Task Post_WhenReplayProtectionEnabled_RejectsStaleAndDuplicateRequestsAcrossRestart()
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

        var databaseName = $"tradeops_signal_replay_{Guid.NewGuid():N}";
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

        var payload = new
        {
            symbol = "BTCUSDT",
            side = "Buy",
            quantity = 0.001m,
            source = "signal-replay-test",
            signalId = Guid.Parse("93939393-9393-4393-9393-939393939393")
        };

        var requestId = Guid.Parse("94949494-9494-4494-9494-949494949494");

        try
        {
            using (var factory = CreateFactory(testBuilder.ConnectionString))
            using (var client = factory.CreateClient())
            {
                using (var missingReplayHeaders = new HttpRequestMessage(HttpMethod.Post, "/api/signals"))
                {
                    missingReplayHeaders.Headers.Add(ApiKeyHeader, ApiKey);
                    missingReplayHeaders.Content = JsonContent.Create(payload);

                    using var response = await client.SendAsync(missingReplayHeaders);
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                }

                using (var staleRequest = CreateSignalRequest(
                           payload,
                           requestId,
                           DateTimeOffset.UtcNow.AddMinutes(-10)))
                {
                    using var response = await client.SendAsync(staleRequest);
                    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                }

                await AssertStateAsync(factory.Services, 0, 0, 0);

                var freshTimestamp = DateTimeOffset.UtcNow;
                using (var acceptedRequest = CreateSignalRequest(
                           payload,
                           requestId,
                           freshTimestamp))
                {
                    using var response = await client.SendAsync(acceptedRequest);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                }

                await AssertStateAsync(factory.Services, 1, 1, 1);

                using (var duplicateRequest = CreateSignalRequest(
                           payload,
                           requestId,
                           freshTimestamp))
                {
                    using var response = await client.SendAsync(duplicateRequest);
                    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                }

                await AssertStateAsync(factory.Services, 1, 1, 1);
            }

            using (var restartedFactory = CreateFactory(testBuilder.ConnectionString))
            using (var restartedClient = restartedFactory.CreateClient())
            using (var replayAfterRestart = CreateSignalRequest(
                       payload,
                       requestId,
                       DateTimeOffset.UtcNow))
            {
                using var response = await restartedClient.SendAsync(replayAfterRestart);
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

                await AssertStateAsync(restartedFactory.Services, 1, 1, 1);
            }
        }
        finally
        {
            await using var dropDatabase = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
                adminConnection);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(string connectionString)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:TradeOpsDb", connectionString);
                builder.UseSetting("Exchange:Provider", "Mock");
                builder.UseSetting("Telegram:Enabled", "false");
                builder.UseSetting("SignalIngress:Authentication:Enabled", "true");
                builder.UseSetting("SignalIngress:Authentication:HeaderName", ApiKeyHeader);
                builder.UseSetting("SignalIngress:Authentication:ApiKey", ApiKey);
                builder.UseSetting("SignalIngress:ReplayProtection:Enabled", "true");
                builder.UseSetting("SignalIngress:ReplayProtection:TimestampHeaderName", TimestampHeader);
                builder.UseSetting("SignalIngress:ReplayProtection:RequestIdHeaderName", RequestIdHeader);
                builder.UseSetting("SignalIngress:ReplayProtection:AllowedClockSkewSeconds", "300");
                builder.UseSetting("SignalIngress:ReplayProtection:ReceiptRetentionSeconds", "600");
            });
    }

    private static HttpRequestMessage CreateSignalRequest(
        object payload,
        Guid requestId,
        DateTimeOffset timestamp)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/signals");
        request.Headers.Add(ApiKeyHeader, ApiKey);
        request.Headers.Add(TimestampHeader, timestamp.ToUnixTimeSeconds().ToString());
        request.Headers.Add(RequestIdHeader, requestId.ToString());
        request.Content = JsonContent.Create(payload);
        return request;
    }

    private static async Task AssertStateAsync(
        IServiceProvider services,
        int expectedReceipts,
        int expectedSignals,
        int expectedOrders)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradeOpsDbContext>();

        Assert.Equal(
            expectedReceipts,
            await dbContext.SignalIngressReplayReceipts.CountAsync());
        Assert.Equal(
            expectedSignals,
            await dbContext.TradingSignals.CountAsync());
        Assert.Equal(
            expectedOrders,
            await dbContext.Orders.CountAsync());
    }
}
