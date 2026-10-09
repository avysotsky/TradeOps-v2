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

public sealed class SignalAuditPaginationApiIntegrationTests
{
    private const string NextCursorHeader = "X-Next-Cursor";

    [Fact]
    public async Task GetSignals_CursorPagination_PreservesArrayContractAndRejectsInvalidCursorUsage()
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

        var databaseName = $"tradeops_signal_audit_pagination_api_{Guid.NewGuid():N}";
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

            var baseTime = DateTimeOffset.Parse("2026-10-02T10:00:00Z");
            await SeedSignalsAsync(
                factory.Services,
                NewSignal(1, "BTCUSDT", baseTime),
                NewSignal(2, "BTCUSDT", baseTime),
                NewSignal(3, "BTCUSDT", baseTime.AddMinutes(1)),
                NewSignal(4, "BTCUSDT", baseTime.AddMinutes(1)),
                NewSignal(5, "BTCUSDT", baseTime.AddMinutes(2)),
                NewSignal(6, "ETHUSDT", baseTime.AddMinutes(3)));

            var seen = new List<Guid>();

            using var first = await client.GetAsync("/api/signals?symbol=BTCUSDT&outcome=Received&limit=2");
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            var firstIds = await ReadSignalIdsAsync(first);
            Assert.Equal(2, firstIds.Count);
            seen.AddRange(firstIds);
            var firstCursor = GetRequiredCursor(first);

            using var second = await client.GetAsync(
                "/api/signals?symbol=BTCUSDT&outcome=Received&limit=2&cursor=" +
                Uri.EscapeDataString(firstCursor));
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            var secondIds = await ReadSignalIdsAsync(second);
            Assert.Equal(2, secondIds.Count);
            Assert.Empty(firstIds.Intersect(secondIds));
            seen.AddRange(secondIds);
            var secondCursor = GetRequiredCursor(second);

            using var third = await client.GetAsync(
                "/api/signals?symbol=BTCUSDT&outcome=Received&limit=2&cursor=" +
                Uri.EscapeDataString(secondCursor));
            Assert.Equal(HttpStatusCode.OK, third.StatusCode);
            var thirdIds = await ReadSignalIdsAsync(third);
            Assert.Single(thirdIds);
            Assert.Empty(seen.Intersect(thirdIds));
            seen.AddRange(thirdIds);
            Assert.False(third.Headers.Contains(NextCursorHeader));

            Assert.Equal(5, seen.Count);
            Assert.Equal(5, seen.Distinct().Count());

            using var changedFilter = await client.GetAsync(
                "/api/signals?symbol=ETHUSDT&outcome=Received&limit=2&cursor=" +
                Uri.EscapeDataString(firstCursor));
            Assert.Equal(HttpStatusCode.BadRequest, changedFilter.StatusCode);

            using var malformed = await client.GetAsync(
                "/api/signals?symbol=BTCUSDT&outcome=Received&limit=2&cursor=not-a-valid-cursor");
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

            using var oversized = await client.GetAsync(
                "/api/signals?cursor=" + new string('a', 1025));
            Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
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

    private static async Task<IReadOnlyCollection<Guid>> ReadSignalIdsAsync(
        HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);

        return document.RootElement
            .EnumerateArray()
            .Select(item => item.GetProperty("signalId").GetGuid())
            .ToArray();
    }

    private static string GetRequiredCursor(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues(NextCursorHeader, out var values));
        return Assert.Single(values);
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

    private static TradingSignal NewSignal(
        int idSuffix,
        string symbol,
        DateTimeOffset createdAt) =>
        new()
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{idSuffix:000000000000}"),
            Symbol = symbol,
            Side = OrderSide.Buy,
            SignalType = "External",
            RequestedQuantity = 0.001m,
            CreatedAt = createdAt,
            Source = "SignalAuditPaginationApiTest",
            Outcome = SignalOutcome.Received
        };
}
