using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class ResearchPreviewHttpIntegrationTests
{
    [Fact]
    public async Task AuthenticatedHttpRoutesUseExistingApplicationContracts()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable("TRADEOPS_TEST_POSTGRES_ADMIN");
        if (string.IsNullOrWhiteSpace(adminConnectionString) &&
            !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true",
                StringComparison.OrdinalIgnoreCase))
            return;

        adminConnectionString ??= "Host=127.0.0.1;Port=5432;Database=postgres;Username=postgres;Password=postgres";
        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = "postgres", Pooling = false
        };
        var databaseName = $"tradeops_arch02_{Guid.NewGuid():N}";
        var testBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
        {
            Database = databaseName, Pooling = false
        };

        await using var connection = new NpgsqlConnection(adminBuilder.ConnectionString);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection))
            await create.ExecuteNonQueryAsync();

        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:TradeOpsDb", testBuilder.ConnectionString);
                builder.UseSetting("Exchange:Provider", "Mock");
                builder.UseSetting("Telegram:Enabled", "false");
                builder.UseSetting("ResearchPreview:Enabled", "true");
                builder.UseSetting("OperatorApi:Authentication:Enabled", "true");
                builder.UseSetting("OperatorApi:Authentication:ApiKey", "arch02-fixture-key");
                builder.UseSetting("OperatorApi:Authentication:HeaderName", "X-TradeOps-Operator-Key");
            });
            using var client = factory.CreateClient();
            var instrument = new InstrumentReference("SAMP", AssetClass.Stock, "USD");
            var decision = new ResearchDecision("sample", "sample-strategy", instrument,
                ResearchDecisionAction.SetTargetWeight,
                new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), TargetWeight: 0.30m);
            var portfolio = new PortfolioSnapshot("USD", 10000m, 10000m,
                Array.Empty<PortfolioPosition>(),
                new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

            var noAuth = await client.PostAsJsonAsync("/api/v1/research/decisions", decision);
            Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);

            client.DefaultRequestHeaders.Add("X-TradeOps-Operator-Key", "arch02-fixture-key");
            var research = await client.PostAsJsonAsync("/api/v1/research/decisions", decision);
            Assert.Equal(HttpStatusCode.OK, research.StatusCode);
            var rebalance = await client.PostAsJsonAsync("/api/v1/rebalance/preview",
                new { decision, portfolio, referencePrice = 100m });
            Assert.Equal(HttpStatusCode.OK, rebalance.StatusCode);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter());
            var plan = await rebalance.Content.ReadFromJsonAsync<RebalancePlan>(options);
            Assert.NotNull(plan);
            Assert.Equal(RebalancePlanStatus.Ready, plan.Status);

            var riskSettings = new RiskSettings
            {
                AllowedSymbols = new HashSet<string> { "SAMP" },
                MaxPositionSize = 100m, MaxOrderSize = 100m,
                MaxDailyLoss = 500m, MaxOpenPositions = 5
            };
            var controls = new RiskControlSnapshot(true, false, null, "USD",
                0m, 0m, 0m, Array.Empty<UnconvertedFee>(), 0, portfolio.AsOf);
            var riskPreview = await client.PostAsJsonAsync("/api/v1/risk/preview",
                new
                {
                    signalId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    symbol = "SAMP", side = OrderSide.Buy, quantity = 30m,
                    createdAt = portfolio.AsOf,
                    settings = riskSettings, controls, positions = Array.Empty<object>()
                });
            Assert.Equal(HttpStatusCode.OK, riskPreview.StatusCode);
            using (var riskJson = await System.Text.Json.JsonDocument.ParseAsync(
                await riskPreview.Content.ReadAsStreamAsync()))
            {
                Assert.True(riskJson.RootElement.GetProperty("allowed").GetBoolean());
            }

            var preview = await client.PostAsJsonAsync("/api/v1/execution/dry-run",
                new { plan, settings = riskSettings, controls, positions = Array.Empty<object>() });
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            var dryRun = await preview.Content.ReadFromJsonAsync<
                TradeOps.Application.Services.RebalanceExecutionDryRunResult>(options);
            Assert.NotNull(dryRun);
            Assert.Equal("Prepared", dryRun.State);
            Assert.False(dryRun.MutationPerformed);
            Assert.False(dryRun.BrokerRequestSent);
            Assert.False(dryRun.PersistencePerformed);
            Assert.StartsWith("trd-", dryRun.ClientOrderId);
        }
        finally
        {
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
