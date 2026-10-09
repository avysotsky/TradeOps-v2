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

public sealed class SignalAuditApiIntegrationTests
{
    [Fact]
    public async Task GetSignals_FiltersNormalizesAndRejectsInvalidAuditQueriesAgainstPostgres()
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

        var databaseName = $"tradeops_signal_audit_api_{Guid.NewGuid():N}";
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
            var btcAcceptedId = Guid.Parse("11111111-1111-4111-8111-111111111111");
            var btcRejectedId = Guid.Parse("22222222-2222-4222-8222-222222222222");
            var ethAcceptedId = Guid.Parse("33333333-3333-4333-8333-333333333333");

            var btcAccepted = NewSignal(btcAcceptedId, "BTCUSDT", SignalOutcome.Accepted, fromUtc.AddMinutes(15));
            var btcRejected = NewSignal(btcRejectedId, "BTCUSDT", SignalOutcome.Rejected, fromUtc.AddMinutes(30));
            btcRejected.ExecutionIssueCode = "ClientOrderIdConflict";
            btcRejected.ExecutionIssueMessage = "Representative issue";
            btcRejected.ExecutionIssueAt = fromUtc.AddMinutes(31);
            var ethAccepted = NewSignal(ethAcceptedId, "ETHUSDT", SignalOutcome.Accepted, fromUtc.AddMinutes(45));

            await SeedSignalsAsync(
                factory.Services,
                btcAccepted,
                btcRejected,
                ethAccepted,
                NewSignal(
                    Guid.Parse("44444444-4444-4444-8444-444444444444"),
                    "BTCUSDT",
                    SignalOutcome.Accepted,
                    toUtc));

            var fromLocal = DateTimeOffset.Parse("2026-10-01T10:00:00+02:00");
            var toLocal = DateTimeOffset.Parse("2026-10-01T11:00:00+02:00");

            using (var filteredResponse = await client.GetAsync(
                       BuildUri(fromLocal, toLocal) +
                       "&symbol=%20btcusdt%20&outcome=Accepted&limit=10"))
            {
                Assert.Equal(HttpStatusCode.OK, filteredResponse.StatusCode);

                using var document = JsonDocument.Parse(await filteredResponse.Content.ReadAsStringAsync());
                var items = document.RootElement;
                Assert.Equal(1, items.GetArrayLength());
                Assert.Equal(btcAcceptedId, items[0].GetProperty("signalId").GetGuid());
                Assert.Equal("BTCUSDT", items[0].GetProperty("symbol").GetString());
                Assert.Equal("Accepted", items[0].GetProperty("outcome").GetString());
            }

            using (var issueResponse = await client.GetAsync(
                       BuildUri(fromLocal, toLocal) +
                       "&executionIssueCode=%20ClientOrderIdConflict%20&outcome=Rejected&limit=10"))
            {
                Assert.Equal(HttpStatusCode.OK, issueResponse.StatusCode);

                using var document = JsonDocument.Parse(await issueResponse.Content.ReadAsStringAsync());
                var items = document.RootElement;
                Assert.Equal(1, items.GetArrayLength());
                Assert.Equal(btcRejectedId, items[0].GetProperty("signalId").GetGuid());
                Assert.Equal("ClientOrderIdConflict", items[0].GetProperty("executionIssueCode").GetString());
            }

            using (var unknownIssueResponse = await client.GetAsync(
                       BuildUri(fromLocal, toLocal) +
                       "&executionIssueCode=UnknownIssue"))
            {
                Assert.Equal(HttpStatusCode.OK, unknownIssueResponse.StatusCode);

                using var document = JsonDocument.Parse(await unknownIssueResponse.Content.ReadAsStringAsync());
                Assert.Equal(0, document.RootElement.GetArrayLength());
            }

            using (var windowResponse = await client.GetAsync(BuildUri(fromLocal, toLocal) + "&limit=2"))
            {
                Assert.Equal(HttpStatusCode.OK, windowResponse.StatusCode);

                using var document = JsonDocument.Parse(await windowResponse.Content.ReadAsStringAsync());
                var items = document.RootElement;
                Assert.Equal(2, items.GetArrayLength());
                Assert.Equal(ethAcceptedId, items[0].GetProperty("signalId").GetGuid());
                Assert.Equal(btcRejectedId, items[1].GetProperty("signalId").GetGuid());
            }

            await AssertBadRequestAsync(client, "/api/signals?limit=0");
            await AssertBadRequestAsync(client, "/api/signals?limit=201");
            await AssertBadRequestAsync(client, "/api/signals?symbol=" + new string('A', 51));
            await AssertBadRequestAsync(client, "/api/signals?executionIssueCode=" + new string('A', 51));
            await AssertBadRequestAsync(client, "/api/signals?outcome=NotARealOutcome");
            await AssertBadRequestAsync(
                client,
                "/api/signals?from=2026-10-01T10%3A00%3A00Z&to=2026-10-01T10%3A00%3A00Z");
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

    private static async Task SeedSignalsAsync(
        IServiceProvider services,
        params TradingSignal[] signals)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradeOpsDbContext>();
        dbContext.TradingSignals.AddRange(signals);
        await dbContext.SaveChangesAsync();
    }

    private static async Task AssertBadRequestAsync(HttpClient client, string uri)
    {
        using var response = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string BuildUri(DateTimeOffset from, DateTimeOffset to) =>
        "/api/signals" +
        $"?from={Uri.EscapeDataString(from.ToString("O"))}" +
        $"&to={Uri.EscapeDataString(to.ToString("O"))}";

    private static TradingSignal NewSignal(
        Guid id,
        string symbol,
        SignalOutcome outcome,
        DateTimeOffset createdAt) =>
        new()
        {
            Id = id,
            Symbol = symbol,
            Side = OrderSide.Buy,
            SignalType = "External",
            RequestedQuantity = 1m,
            CreatedAt = createdAt,
            Source = "SignalAuditApiIntegrationTest",
            Outcome = outcome
        };
}
