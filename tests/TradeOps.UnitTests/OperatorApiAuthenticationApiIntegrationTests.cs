using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class OperatorApiAuthenticationApiIntegrationTests
{
    private const string OperatorHeader = "X-TradeOps-Operator-Key";
    private const string OperatorKey = "operator-integration-secret";
    private const string SignalHeader = "X-TradeOps-Api-Key";
    private const string SignalKey = "signal-integration-secret";

    [Fact]
    public async Task ProtectedActions_AndTradingViewOperations_RequireOperatorCredential()
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

        var databaseName = $"tradeops_operator_auth_{Guid.NewGuid():N}";
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
                    builder.UseSetting("SignalIngress:Authentication:HeaderName", SignalHeader);
                    builder.UseSetting("SignalIngress:Authentication:ApiKey", SignalKey);
                    builder.UseSetting("OperatorApi:Authentication:Enabled", "true");
                    builder.UseSetting("OperatorApi:Authentication:HeaderName", OperatorHeader);
                    builder.UseSetting("OperatorApi:Authentication:ApiKey", OperatorKey);
                });

            client = factory.CreateClient();

            using (var readOnlyRisk = await client.GetAsync("/api/risk"))
            {
                Assert.Equal(HttpStatusCode.OK, readOnlyRisk.StatusCode);
            }

            var protectedRequests = new[]
            {
                new RequestCase(HttpMethod.Post, "/api/risk/trading-enabled", new { enabled = false }),
                new RequestCase(HttpMethod.Post, "/api/risk/emergency-stop", new { enabled = true, reason = "auth-test" }),
                new RequestCase(HttpMethod.Delete, "/api/orders/missing-exchange-order", null),
                new RequestCase(HttpMethod.Post, "/api/orders/local/missing-local-order/cancel", null),
                new RequestCase(HttpMethod.Post, "/api/orders/local/cancel-all", null),
                new RequestCase(HttpMethod.Post, "/api/system/reconcile", null),
                new RequestCase(HttpMethod.Post, "/api/system/reconcile/positions", null),
                new RequestCase(HttpMethod.Get, "/api/integrations/tradingview/operations/deliveries", null),
                new RequestCase(HttpMethod.Get, "/api/integrations/tradingview/operations/metrics", null),
                new RequestCase(HttpMethod.Get, "/api/integrations/tradingview/operations/health", null),
                new RequestCase(HttpMethod.Get, "/api/signals/92929292-9292-4292-9292-929292929292", null),
                new RequestCase(HttpMethod.Get, "/api/orders/local/missing-local-order", null),
                new RequestCase(HttpMethod.Get, "/api/orders/local/missing-local-order/history", null),
                new RequestCase(HttpMethod.Get, "/api/system/reconciliation/status", null)
            };

            foreach (var requestCase in protectedRequests)
            {
                using var response = await SendAsync(client, requestCase);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            using (var wrongOperatorKey = await SendAsync(
                       client,
                       new RequestCase(HttpMethod.Post, "/api/system/reconcile", null),
                       OperatorHeader,
                       "wrong-operator-secret"))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, wrongOperatorKey.StatusCode);
            }

            using (var signalKeyOnOperatorRoute = await SendAsync(
                       client,
                       new RequestCase(HttpMethod.Post, "/api/system/reconcile", null),
                       OperatorHeader,
                       SignalKey))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, signalKeyOnOperatorRoute.StatusCode);
            }

            using (var validOperatorKey = await SendAsync(
                       client,
                       new RequestCase(HttpMethod.Post, "/api/system/reconcile", null),
                       OperatorHeader,
                       OperatorKey))
            {
                Assert.Equal(HttpStatusCode.OK, validOperatorKey.StatusCode);
            }

            using (var validTradingViewRead = await SendAsync(
                       client,
                       new RequestCase(HttpMethod.Get, "/api/integrations/tradingview/operations/metrics", null),
                       OperatorHeader,
                       OperatorKey))
            {
                Assert.Equal(HttpStatusCode.OK, validTradingViewRead.StatusCode);
            }

            var signalPayload = new
            {
                symbol = "BTCUSDT",
                side = "Buy",
                quantity = 0.001m,
                source = "operator-auth-separation-test",
                signalId = Guid.Parse("92929292-9292-4292-9292-929292929292")
            };

            using (var operatorKeyOnlySignalRequest = new HttpRequestMessage(HttpMethod.Post, "/api/signals"))
            {
                operatorKeyOnlySignalRequest.Headers.Add(OperatorHeader, OperatorKey);
                operatorKeyOnlySignalRequest.Content = JsonContent.Create(signalPayload);

                using var response = await client.SendAsync(operatorKeyOnlySignalRequest);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            using (var validSignalRequest = new HttpRequestMessage(HttpMethod.Post, "/api/signals"))
            {
                validSignalRequest.Headers.Add(SignalHeader, SignalKey);
                validSignalRequest.Content = JsonContent.Create(signalPayload);

                using var response = await client.SendAsync(validSignalRequest);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        RequestCase requestCase,
        string? headerName = null,
        string? headerValue = null)
    {
        using var request = new HttpRequestMessage(requestCase.Method, requestCase.Path);

        if (headerName is not null && headerValue is not null)
        {
            request.Headers.Add(headerName, headerValue);
        }

        if (requestCase.Body is not null)
        {
            request.Content = JsonContent.Create(requestCase.Body);
        }

        return await client.SendAsync(request);
    }

    private sealed record RequestCase(HttpMethod Method, string Path, object? Body);
}
