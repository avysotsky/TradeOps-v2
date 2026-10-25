using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class SignalIngressRequestAuditApiIntegrationTests
{
    private const string ApiKeyHeader = "X-TradeOps-Api-Key";
    private const string ApiKey = "audit-integration-api-key";
    private const string TimestampHeader = "X-TradeOps-Timestamp";
    private const string RequestIdHeader = "X-TradeOps-Request-Id";
    private const string SignatureHeader = "X-TradeOps-Signature";
    private const string SigningSecret =
        "audit-integration-signing-secret-32-plus";

    [Fact]
    public async Task RequestAudit_PreservesSecurityAttempts_AndLinksAcceptedExecution()
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
            $"tradeops_ingress_audit_{Guid.NewGuid():N}";
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

        var signalId = Guid.Parse(
            "98989898-9898-4898-9898-989898989898");
        var requestId = Guid.Parse(
            "99999999-9898-4898-9898-989898989898");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var body =
            $"{{\"symbol\":\"BTCUSDT\",\"side\":\"Buy\",\"quantity\":0.001,\"source\":\"ingress-audit-test\",\"signalId\":\"{signalId:D}\"}}";
        var tamperedBody =
            $"{{\"symbol\":\"BTCUSDT\",\"side\":\"Buy\",\"quantity\":0.002,\"source\":\"ingress-audit-test\",\"signalId\":\"{signalId:D}\"}}";

        try
        {
            using var factory = CreateFactory(
                testBuilder.ConnectionString);
            using var client = factory.CreateClient();

            var signature = ComputeSignature(
                timestamp,
                requestId,
                body);

            using (var tamperedRequest = CreateRequest(
                       tamperedBody,
                       timestamp,
                       requestId,
                       signature))
            {
                using var response = await client.SendAsync(
                    tamperedRequest);
                Assert.Equal(
                    HttpStatusCode.Unauthorized,
                    response.StatusCode);
                Assert.True(
                    response.Headers.Contains(
                        "X-TradeOps-Ingress-Audit-Id"));
            }

            using (var validRequest = CreateRequest(
                       body,
                       timestamp,
                       requestId,
                       signature))
            {
                using var response = await client.SendAsync(
                    validRequest);
                Assert.Equal(
                    HttpStatusCode.OK,
                    response.StatusCode);
            }

            using (var replayRequest = CreateRequest(
                       body,
                       timestamp,
                       requestId,
                       signature))
            {
                using var response = await client.SendAsync(
                    replayRequest);
                Assert.Equal(
                    HttpStatusCode.Conflict,
                    response.StatusCode);
            }

            using var auditResponse = await client.GetAsync(
                $"/api/signal-ingress/requests/{requestId:D}");
            Assert.Equal(
                HttpStatusCode.OK,
                auditResponse.StatusCode);

            await using var stream =
                await auditResponse.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            var attempts = json.RootElement;

            Assert.Equal(3, attempts.GetArrayLength());

            Assert.Equal(
                "SignatureRejected",
                attempts[0].GetProperty("outcome").GetString());
            Assert.Equal(
                401,
                attempts[0].GetProperty("httpStatusCode").GetInt32());
            Assert.Equal(
                JsonValueKind.Null,
                attempts[0].GetProperty("signalId").ValueKind);

            Assert.Equal(
                "Accepted",
                attempts[1].GetProperty("outcome").GetString());
            Assert.Equal(
                200,
                attempts[1].GetProperty("httpStatusCode").GetInt32());
            Assert.Equal(
                signalId,
                attempts[1].GetProperty("signalId").GetGuid());
            Assert.NotEqual(
                Guid.Empty,
                attempts[1].GetProperty("orderId").GetGuid());
            Assert.StartsWith(
                "trd-",
                attempts[1]
                    .GetProperty("clientOrderId")
                    .GetString());

            Assert.Equal(
                "ReplayRejected",
                attempts[2].GetProperty("outcome").GetString());
            Assert.Equal(
                409,
                attempts[2].GetProperty("httpStatusCode").GetInt32());

            foreach (var attempt in attempts.EnumerateArray())
            {
                Assert.Equal(
                    requestId,
                    attempt.GetProperty("requestId").GetGuid());
                Assert.Equal(
                    "POST",
                    attempt.GetProperty("method").GetString());
                Assert.Equal(
                    "/api/signals",
                    attempt.GetProperty("path").GetString());
                Assert.NotEqual(
                    JsonValueKind.Null,
                    attempt.GetProperty("completedAt").ValueKind);
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

    private static WebApplicationFactory<Program> CreateFactory(
        string connectionString)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting(
                    "ConnectionStrings:TradeOpsDb",
                    connectionString);
                builder.UseSetting(
                    "Exchange:Provider",
                    "Mock");
                builder.UseSetting(
                    "Telegram:Enabled",
                    "false");
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
        string signature)
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
        request.Headers.Add(
            SignatureHeader,
            signature);

        request.Content = new StringContent(
            body,
            Encoding.UTF8,
            "application/json");

        return request;
    }

    private static string ComputeSignature(
        long timestamp,
        Guid requestId,
        string body)
    {
        var prefix = string.Join(
            "\n",
            timestamp.ToString(CultureInfo.InvariantCulture),
            requestId.ToString("D"),
            "POST",
            "/api/signals",
            string.Empty);

        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var payload = new byte[
            prefixBytes.Length + bodyBytes.Length];

        prefixBytes.CopyTo(payload, 0);
        bodyBytes.CopyTo(payload, prefixBytes.Length);

        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(SigningSecret),
            payload);

        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
