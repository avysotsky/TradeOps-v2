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

public sealed class SignalTransitionMetricsApiIntegrationTests
{
    [Fact]
    public async Task Api_ExposesOpenApiAndAllSignalTransitionMetricRoutesAgainstPostgres()
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

        var databaseName = $"tradeops_transition_api_{Guid.NewGuid():N}";
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

            var from = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
            var to = DateTimeOffset.Parse("2026-10-01T11:00:00Z");
            await SeedTransitionHistoryAsync(factory.Services, from);

            await AssertOpenApiRoutesAsync(client);
            await AssertWindowRouteAsync(client, from, to);
            await AssertSeriesRouteAsync(client, from, to);
            await AssertBySymbolRouteAsync(client, from, to);
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

    private static async Task SeedTransitionHistoryAsync(
        IServiceProvider services,
        DateTimeOffset from)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradeOpsDbContext>();

        var btc = NewSignal(
            Guid.Parse("11111111-1111-4111-8111-111111111111"),
            "BTCUSDT",
            SignalOutcome.Accepted);
        var eth = NewSignal(
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            "ETHUSDT",
            SignalOutcome.Rejected);
        var legacy = NewSignal(
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            "XRPUSDT",
            SignalOutcome.Accepted);

        dbContext.TradingSignals.AddRange(btc, eth, legacy);
        await dbContext.SaveChangesAsync();

        dbContext.TradingSignalOutcomeEvents.AddRange(
            NewEvent(btc.Id, SignalOutcome.Received, from.AddMinutes(5)),
            NewEvent(btc.Id, SignalOutcome.Accepted, from.AddMinutes(6)),
            NewEvent(eth.Id, SignalOutcome.Received, from.AddMinutes(20)),
            NewEvent(eth.Id, SignalOutcome.Rejected, from.AddMinutes(21)));
        await dbContext.SaveChangesAsync();
    }

    private static async Task AssertOpenApiRoutesAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");

        Assert.True(paths.TryGetProperty("/api/metrics/signal-transitions/window", out _));
        Assert.True(paths.TryGetProperty("/api/metrics/signal-transitions/series", out _));
        Assert.True(paths.TryGetProperty("/api/metrics/signal-transitions/by-symbol", out _));
    }

    private static async Task AssertWindowRouteAsync(
        HttpClient client,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        using var response = await client.GetAsync(BuildUri(
            "/api/metrics/signal-transitions/window",
            from,
            to));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("receivedTransitions").GetInt64());
        Assert.Equal(1, root.GetProperty("acceptedTransitions").GetInt64());
        Assert.Equal(1, root.GetProperty("rejectedTransitions").GetInt64());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("symbol").ValueKind);
    }

    private static async Task AssertSeriesRouteAsync(
        HttpClient client,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        using var response = await client.GetAsync(
            BuildUri(
                "/api/metrics/signal-transitions/series",
                from,
                to) + "&bucket=15m");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var buckets = root.GetProperty("buckets");

        Assert.Equal("15m", root.GetProperty("bucket").GetString());
        Assert.Equal(4, buckets.GetArrayLength());

        Assert.Equal(1, buckets[0].GetProperty("receivedTransitions").GetInt64());
        Assert.Equal(1, buckets[0].GetProperty("acceptedTransitions").GetInt64());
        Assert.Equal(0, buckets[0].GetProperty("rejectedTransitions").GetInt64());

        Assert.Equal(1, buckets[1].GetProperty("receivedTransitions").GetInt64());
        Assert.Equal(0, buckets[1].GetProperty("acceptedTransitions").GetInt64());
        Assert.Equal(1, buckets[1].GetProperty("rejectedTransitions").GetInt64());

        Assert.Equal(0, buckets[2].GetProperty("receivedTransitions").GetInt64());
        Assert.Equal(0, buckets[3].GetProperty("receivedTransitions").GetInt64());
    }

    private static async Task AssertBySymbolRouteAsync(
        HttpClient client,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        using var response = await client.GetAsync(
            BuildUri(
                "/api/metrics/signal-transitions/by-symbol",
                from,
                to) + "&limit=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var symbols = root.GetProperty("symbols");

        Assert.Equal(1, root.GetProperty("limit").GetInt32());
        Assert.True(root.GetProperty("isTruncated").GetBoolean());
        Assert.Equal(1, symbols.GetArrayLength());
        Assert.Equal("BTCUSDT", symbols[0].GetProperty("symbol").GetString());
        Assert.Equal(2, symbols[0].GetProperty("totalTransitions").GetInt64());
        Assert.Equal(1, symbols[0].GetProperty("receivedTransitions").GetInt64());
        Assert.Equal(1, symbols[0].GetProperty("acceptedTransitions").GetInt64());
        Assert.Equal(0, symbols[0].GetProperty("rejectedTransitions").GetInt64());
    }

    private static string BuildUri(
        string path,
        DateTimeOffset from,
        DateTimeOffset to) =>
        $"{path}?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

    private static TradingSignal NewSignal(
        Guid id,
        string symbol,
        SignalOutcome outcome) =>
        new()
        {
            Id = id,
            Symbol = symbol,
            Side = OrderSide.Buy,
            SignalType = "External",
            RequestedQuantity = 1m,
            CreatedAt = DateTimeOffset.Parse("2026-09-30T00:00:00Z"),
            Source = "SignalTransitionApiIntegrationTest",
            Outcome = outcome
        };

    private static TradingSignalOutcomeEvent NewEvent(
        Guid signalId,
        SignalOutcome outcome,
        DateTimeOffset occurredAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            TradingSignalId = signalId,
            PreviousOutcome = outcome == SignalOutcome.Received ? null : SignalOutcome.Received,
            Outcome = outcome,
            OccurredAt = occurredAt,
            RiskRejectionReasons = outcome == SignalOutcome.Rejected
                ? ["Representative rejection"]
                : []
        };
}
