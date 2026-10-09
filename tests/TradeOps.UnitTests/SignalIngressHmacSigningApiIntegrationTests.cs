using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TradeOps.Infrastructure.Persistence;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class SignalIngressHmacSigningApiIntegrationTests
{
    private const string ApiKeyHeader = "X-TradeOps-Api-Key";
    private const string ApiKey = "hmac-integration-api-key";
    private const string TimestampHeader = "X-TradeOps-Timestamp";
    private const string RequestIdHeader = "X-TradeOps-Request-Id";
    private const string SignatureHeader = "X-TradeOps-Signature";
    private const string SigningSecret =
        "hmac-integration-signing-secret-32-plus";

    [Fact]
    public async Task Post_WhenSigningEnabled_BindsBodyAndRegistersNonceOnlyAfterValidSignature()
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

        var databaseName = $"tradeops_signal_hmac_{Guid.NewGuid():N}";
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

        const string body =
            "{\"symbol\":\"BTCUSDT\",\"side\":\"Buy\",\"quantity\":0.001,\"source\":\"hmac-test\",\"signalId\":\"95959595-9595-4595-9595-959595959595\"}";
        const string tamperedBody =
            "{\"symbol\":\"BTCUSDT\",\"side\":\"Buy\",\"quantity\":0.002,\"source\":\"hmac-test\",\"signalId\":\"96969696-9696-4696-9696-969696969696\"}";

        try
        {
            using var factory = CreateFactory(testBuilder.ConnectionString);
            using var client = factory.CreateClient();

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var requestId = Guid.Parse("97979797-9797-4797-9797-979797979797");

            using (var missingSignature = CreateRequest(
                       body,
                       timestamp,
                       requestId,
                       signature: null))
            {
                using var response = await client.SendAsync(missingSignature);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            await AssertStateAsync(factory.Services, 0, 0, 0);

            using (var wrongPathSignature = CreateRequest(
                       body,
                       timestamp,
                       requestId,
                       signature: ComputeSignature(
                           timestamp,
                           requestId,
                           HttpMethod.Post.Method,
                           "/api/not-signals",
                           body)))
            {
                using var response = await client.SendAsync(wrongPathSignature);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            await AssertStateAsync(factory.Services, 0, 0, 0);

            using (var tamperedRequest = CreateRequest(
                       tamperedBody,
                       timestamp,
                       requestId,
                       signature: ComputeSignature(
                           timestamp,
                           requestId,
                           HttpMethod.Post.Method,
                           "/api/signals",
                           body)))
            {
                using var response = await client.SendAsync(tamperedRequest);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            await AssertStateAsync(factory.Services, 0, 0, 0);

            var validSignature = ComputeSignature(
                timestamp,
                requestId,
                HttpMethod.Post.Method,
                "/api/signals",
                body);

            using (var validRequest = CreateRequest(
                       body,
                       timestamp,
                       requestId,
                       validSignature))
            {
                using var response = await client.SendAsync(validRequest);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            await AssertStateAsync(factory.Services, 1, 1, 1);

            using (var duplicateRequest = CreateRequest(
                       body,
                       timestamp,
                       requestId,
                       validSignature))
            {
                using var response = await client.SendAsync(duplicateRequest);
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            }

            await AssertStateAsync(factory.Services, 1, 1, 1);
        }
        finally
        {
            await using var dropDatabase = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
                adminConnection);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string connectionString)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting(
                    "ConnectionStrings:TradeOpsDb",
                    connectionString);
                builder.UseSetting("Exchange:Provider", "Mock");
                builder.UseSetting("Telegram:Enabled", "false");
                builder.UseSetting(
                    "SignalIngress:Authentication:Enabled",
                    "true");
                builder.UseSetting(
                    "SignalIngress:Authentication:HeaderName",
                    ApiKeyHeader);
                builder.UseSetting(
                    "SignalIngress:Authentication:ApiKey",
                    ApiKey);
                builder.UseSetting(
                    "SignalIngress:ReplayProtection:Enabled",
                    "true");
                builder.UseSetting(
                    "SignalIngress:ReplayProtection:TimestampHeaderName",
                    TimestampHeader);
                builder.UseSetting(
                    "SignalIngress:ReplayProtection:RequestIdHeaderName",
                    RequestIdHeader);
                builder.UseSetting(
                    "SignalIngress:ReplayProtection:AllowedClockSkewSeconds",
                    "300");
                builder.UseSetting(
                    "SignalIngress:ReplayProtection:ReceiptRetentionSeconds",
                    "600");
                builder.UseSetting(
                    "SignalIngress:Signing:Enabled",
                    "true");
                builder.UseSetting(
                    "SignalIngress:Signing:SignatureHeaderName",
                    SignatureHeader);
                builder.UseSetting(
                    "SignalIngress:Signing:Secret",
                    SigningSecret);
                builder.UseSetting(
                    "SignalIngress:Signing:MaxBodyBytes",
                    "65536");
            });
    }

    private static HttpRequestMessage CreateRequest(
        string body,
        long timestamp,
        Guid requestId,
        string? signature)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/signals");

        request.Headers.Add(ApiKeyHeader, ApiKey);
        request.Headers.Add(
            TimestampHeader,
            timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(
            RequestIdHeader,
            requestId.ToString("D"));

        if (signature is not null)
        {
            request.Headers.Add(SignatureHeader, signature);
        }

        request.Content = new StringContent(
            body,
            Encoding.UTF8,
            "application/json");

        return request;
    }

    private static string ComputeSignature(
        long timestamp,
        Guid requestId,
        string method,
        string path,
        string body)
    {
        var prefix = string.Join(
            "\n",
            timestamp.ToString(CultureInfo.InvariantCulture),
            requestId.ToString("D"),
            method.ToUpperInvariant(),
            path,
            string.Empty);

        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var payload = new byte[prefixBytes.Length + bodyBytes.Length];

        prefixBytes.CopyTo(payload, 0);
        bodyBytes.CopyTo(payload, prefixBytes.Length);

        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(SigningSecret),
            payload);

        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
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
