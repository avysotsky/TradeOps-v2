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

public sealed class TradingViewWebhookAdapterApiIntegrationTests
{
    private const string GatewayHeader =
        "X-TradeOps-TradingView-Gateway-Key";
    private const string GatewayKey =
        "tradingview-integration-gateway-key-32-plus";

    [Fact]
    public async Task Adapter_AuditsAcceptedRedeliveredAndConflictingDeliveries()
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
            $"tradeops_tradingview_{Guid.NewGuid():N}";
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
            using var factory = CreateFactory(
                testBuilder.ConnectionString);
            using var client = factory.CreateClient();

            const string eventId =
                "BTCUSDT|1h|2026-10-04T15:00:00Z|long-entry";

            var payload = new
            {
                eventId,
                symbol = "btcusdt",
                action = "buy",
                quantity = 0.001m,
                riskPercent = 1.0m
            };

            using (var unauthenticated = await client.PostAsJsonAsync(
                       "/api/integrations/tradingview",
                       payload))
            {
                Assert.Equal(
                    HttpStatusCode.Unauthorized,
                    unauthenticated.StatusCode);
            }

            await AssertCountsAsync(
                factory.Services,
                expectedSignals: 0,
                expectedOrders: 0,
                expectedDeliveries: 0);

            using var firstRequest = CreateGatewayRequest(payload);
            using var firstResponse = await client.SendAsync(firstRequest);
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            Assert.True(
                firstResponse.Headers.Contains(
                    "X-TradeOps-TradingView-Delivery-Id"));

            var first = await firstResponse.Content
                .ReadFromJsonAsync<TradingViewResponse>();
            Assert.NotNull(first);
            Assert.True(first.Accepted);
            Assert.NotEqual(Guid.Empty, first.SignalId);
            Assert.NotNull(first.Order);
            Assert.StartsWith(
                "trd-",
                first.Order.ClientOrderId);

            await AssertCountsAsync(
                factory.Services,
                expectedSignals: 1,
                expectedOrders: 1,
                expectedDeliveries: 1);

            using var retryRequest = CreateGatewayRequest(payload);
            using var retryResponse = await client.SendAsync(retryRequest);
            Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);

            var retry = await retryResponse.Content
                .ReadFromJsonAsync<TradingViewResponse>();
            Assert.NotNull(retry);
            Assert.Equal(first.SignalId, retry.SignalId);
            Assert.Equal(
                first.Order!.ClientOrderId,
                retry.Order!.ClientOrderId);

            var conflictingPayload = new
            {
                eventId,
                symbol = "BTCUSDT",
                action = "buy",
                quantity = 0.002m,
                riskPercent = 1.0m
            };

            using var conflictRequest =
                CreateGatewayRequest(conflictingPayload);
            using var conflictResponse =
                await client.SendAsync(conflictRequest);

            Assert.Equal(
                HttpStatusCode.Conflict,
                conflictResponse.StatusCode);

            await AssertCountsAsync(
                factory.Services,
                expectedSignals: 1,
                expectedOrders: 1,
                expectedDeliveries: 3);

            using var deliveryResponse = await client.GetAsync(
                $"/api/integrations/tradingview/operations/deliveries?eventId={Uri.EscapeDataString(eventId)}&limit=10");

            Assert.Equal(
                HttpStatusCode.OK,
                deliveryResponse.StatusCode);

            var deliveries = await deliveryResponse.Content
                .ReadFromJsonAsync<TradingViewDelivery[]>();

            Assert.NotNull(deliveries);
            Assert.Equal(3, deliveries.Length);

            var ordered = deliveries
                .OrderBy(item => item.ReceivedAt)
                .ToArray();

            Assert.Equal("Accepted", ordered[0].Outcome);
            Assert.Equal(200, ordered[0].HttpStatusCode);
            Assert.Equal(first.SignalId, ordered[0].SignalId);
            Assert.NotNull(ordered[0].OrderId);
            Assert.Equal(
                first.Order.ClientOrderId,
                ordered[0].ClientOrderId);

            Assert.Equal("Redelivered", ordered[1].Outcome);
            Assert.Equal(200, ordered[1].HttpStatusCode);
            Assert.Equal(first.SignalId, ordered[1].SignalId);
            Assert.Equal(
                first.Order.ClientOrderId,
                ordered[1].ClientOrderId);

            Assert.Equal("Conflict", ordered[2].Outcome);
            Assert.Equal(409, ordered[2].HttpStatusCode);
            Assert.Equal(first.SignalId, ordered[2].SignalId);

            Assert.All(
                ordered,
                item =>
                {
                    Assert.NotNull(item.CompletedAt);
                    Assert.NotNull(item.DurationMilliseconds);
                    Assert.True(item.DurationMilliseconds >= 0);
                    Assert.Equal("BTCUSDT", item.Symbol);
                    Assert.Equal("buy", item.Action);
                });

            using var metricsResponse = await client.GetAsync(
                "/api/integrations/tradingview/operations/metrics");

            Assert.Equal(
                HttpStatusCode.OK,
                metricsResponse.StatusCode);

            var metrics = await metricsResponse.Content
                .ReadFromJsonAsync<TradingViewMetrics>();

            Assert.NotNull(metrics);
            Assert.Equal(3, metrics.Total);
            Assert.Equal(0, metrics.Pending);
            Assert.Equal(1, metrics.Accepted);
            Assert.Equal(1, metrics.Redelivered);
            Assert.Equal(1, metrics.Conflict);
            Assert.Equal(0, metrics.ValidationRejected);
            Assert.NotNull(metrics.AverageLatencyMilliseconds);
            Assert.NotNull(metrics.MaxLatencyMilliseconds);

            using var signalAudit = await client.GetAsync(
                $"/api/signals/{first.SignalId:D}");
            Assert.Equal(
                HttpStatusCode.OK,
                signalAudit.StatusCode);

            var auditJson = await signalAudit.Content
                .ReadFromJsonAsync<TradingSignalAudit>();
            Assert.NotNull(auditJson);
            Assert.Equal("BTCUSDT", auditJson.Symbol);
            Assert.Equal("TradingView", auditJson.SignalType);
            Assert.Equal("TradingView", auditJson.Source);
        }
        finally
        {
            await using var dropDatabase = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
                adminConnection);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Adapter_AuditsValidationRejectionBeforeExecution()
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
            $"tradeops_tradingview_invalid_{Guid.NewGuid():N}";
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
            using var factory = CreateFactory(
                testBuilder.ConnectionString);
            using var client = factory.CreateClient();

            const string eventId = "invalid-action-event";
            var invalid = new
            {
                eventId,
                symbol = "BTCUSDT",
                action = "hold",
                quantity = 0.001m
            };

            using var request = CreateGatewayRequest(invalid);
            using var response = await client.SendAsync(request);

            Assert.Equal(
                HttpStatusCode.BadRequest,
                response.StatusCode);
            Assert.True(
                response.Headers.Contains(
                    "X-TradeOps-TradingView-Delivery-Id"));

            await AssertCountsAsync(
                factory.Services,
                expectedSignals: 0,
                expectedOrders: 0,
                expectedDeliveries: 1);

            using var deliveriesResponse = await client.GetAsync(
                $"/api/integrations/tradingview/operations/deliveries?eventId={eventId}");

            var deliveries = await deliveriesResponse.Content
                .ReadFromJsonAsync<TradingViewDelivery[]>();

            Assert.NotNull(deliveries);
            Assert.Single(deliveries);
            Assert.Equal(
                "ValidationRejected",
                deliveries[0].Outcome);
            Assert.Equal(
                400,
                deliveries[0].HttpStatusCode);
            Assert.Null(deliveries[0].SignalId);
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
                    "Integrations:TradingView:Enabled",
                    "true");
                builder.UseSetting(
                    "Integrations:TradingView:GatewayHeaderName",
                    GatewayHeader);
                builder.UseSetting(
                    "Integrations:TradingView:GatewayKey",
                    GatewayKey);
                builder.UseSetting(
                    "Integrations:TradingView:RequireGatewayIpAllowlist",
                    "false");
            });
    }

    private static HttpRequestMessage CreateGatewayRequest(
        object payload)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/integrations/tradingview");

        request.Headers.Add(
            GatewayHeader,
            GatewayKey);
        request.Content = JsonContent.Create(payload);
        return request;
    }

    private static async Task AssertCountsAsync(
        IServiceProvider services,
        int expectedSignals,
        int expectedOrders,
        int expectedDeliveries)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<TradeOpsDbContext>();

        Assert.Equal(
            expectedSignals,
            await dbContext.TradingSignals.CountAsync());
        Assert.Equal(
            expectedOrders,
            await dbContext.Orders.CountAsync());
        Assert.Equal(
            expectedDeliveries,
            await dbContext.TradingViewDeliveryAudits.CountAsync());
    }

    private sealed record TradingViewResponse(
        string EventId,
        Guid SignalId,
        bool Accepted,
        IReadOnlyCollection<string> RiskReasons,
        OrderResponse? Order);

    private sealed record OrderResponse(
        string? ExchangeOrderId,
        string ClientOrderId,
        string Status,
        decimal FilledQuantity,
        decimal? AverageFillPrice);

    private sealed record TradingSignalAudit(
        Guid SignalId,
        string Symbol,
        string? SignalType,
        string? Source);

    private sealed record TradingViewDelivery(
        Guid DeliveryId,
        string? EventId,
        DateTimeOffset ReceivedAt,
        DateTimeOffset? CompletedAt,
        long? DurationMilliseconds,
        string Outcome,
        int? HttpStatusCode,
        Guid? SignalId,
        Guid? OrderId,
        string? ClientOrderId,
        string? Symbol,
        string? Action);

    private sealed record TradingViewMetrics(
        DateTimeOffset GeneratedAt,
        DateTimeOffset FromInclusive,
        DateTimeOffset ToExclusive,
        int Total,
        int Pending,
        int Accepted,
        int Redelivered,
        int ValidationRejected,
        int RiskRejected,
        int Conflict,
        int Failed,
        double? AverageLatencyMilliseconds,
        long? MaxLatencyMilliseconds);
}
