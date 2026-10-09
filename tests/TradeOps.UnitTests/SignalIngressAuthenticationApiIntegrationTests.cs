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

public sealed class SignalIngressAuthenticationApiIntegrationTests
{
    private const string ApiKeyHeader = "X-TradeOps-Api-Key";
    private const string ApiKey = "integration-test-secret";

    [Fact]
    public async Task Post_WhenAuthenticationEnabled_RejectsMissingOrWrongKeyBeforePersistence()
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

        var databaseName = $"tradeops_signal_ingress_auth_{Guid.NewGuid():N}";
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
                    builder.UseSetting("SignalIngress:Authentication:Enabled", "true");
                    builder.UseSetting("SignalIngress:Authentication:HeaderName", ApiKeyHeader);
                    builder.UseSetting("SignalIngress:Authentication:ApiKey", ApiKey);
                });

            client = factory.CreateClient();

            var payload = new
            {
                symbol = "BTCUSDT",
                side = "Buy",
                quantity = 0.001m,
                source = "signal-ingress-auth-test",
                signalId = Guid.Parse("91919191-9191-4191-9191-919191919191")
            };

            using (var missingKeyResponse = await client.PostAsJsonAsync("/api/signals", payload))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, missingKeyResponse.StatusCode);
            }

            using (var wrongKeyRequest = new HttpRequestMessage(HttpMethod.Post, "/api/signals"))
            {
                wrongKeyRequest.Headers.Add(ApiKeyHeader, "wrong-secret");
                wrongKeyRequest.Content = JsonContent.Create(payload);

                using var wrongKeyResponse = await client.SendAsync(wrongKeyRequest);
                Assert.Equal(HttpStatusCode.Unauthorized, wrongKeyResponse.StatusCode);
            }

            await AssertNoExecutionStateAsync(factory.Services);

            using (var validRequest = new HttpRequestMessage(HttpMethod.Post, "/api/signals"))
            {
                validRequest.Headers.Add(ApiKeyHeader, ApiKey);
                validRequest.Content = JsonContent.Create(payload);

                using var validResponse = await client.SendAsync(validRequest);
                Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
            }

            await using var scope = factory.Services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TradeOpsDbContext>();

            Assert.Equal(1, await dbContext.TradingSignals.CountAsync());
            Assert.Equal(1, await dbContext.Orders.CountAsync());
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

    private static async Task AssertNoExecutionStateAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradeOpsDbContext>();

        Assert.Equal(0, await dbContext.TradingSignals.CountAsync());
        Assert.Equal(0, await dbContext.TradingSignalOutcomeEvents.CountAsync());
        Assert.Equal(0, await dbContext.Orders.CountAsync());
        Assert.Equal(0, await dbContext.Fills.CountAsync());
    }
}
