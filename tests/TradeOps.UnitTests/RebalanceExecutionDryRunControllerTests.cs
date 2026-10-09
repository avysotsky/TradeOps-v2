using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using TradeOps.Api.Controllers;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class RebalanceExecutionDryRunControllerTests
{
    private static RebalanceExecutionDryRunController Controller(bool enabled, bool authenticated) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ResearchPreview:Enabled"] = enabled.ToString(),
            ["OperatorApi:Authentication:Enabled"] = authenticated.ToString()
        }).Build());

    private static ExecutionDryRunHttpRequest Request(bool tradingEnabled = true)
    {
        var at = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var instrument = new InstrumentReference("SAMP", AssetClass.Stock, "USD");
        var intent = new RebalanceOrderIntent("decision-test", instrument, OrderSide.Buy, 30m, 100m, 3000m);
        var plan = new RebalancePlan("decision-test", "strategy-test", at, null,
            0m, 0m, 30m, 3000m, RebalancePlanStatus.Ready, intent,
            Array.Empty<RebalanceConstraintViolation>());
        return new ExecutionDryRunHttpRequest(
            plan,
            new RiskSettings
            {
                AllowedSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SAMP" },
                MaxOrderSize = 100m, MaxPositionSize = 100m,
                MaxDailyLoss = 500m, MaxOpenPositions = 5
            },
            new ResearchRiskControlsRequest(tradingEnabled, false, null, "USD",
                0m, 0m, 0m, Array.Empty<UnconvertedFee>(), 0, at),
            Array.Empty<Position>());
    }

    [Fact]
    public async Task AllowedRouteMatchesApplicationPreview()
    {
        var request = Request();
        var expected = await RebalanceExecutionDryRunPreview.RunAsync(
            request.Plan, request.Settings, request.Controls.ToSnapshot(), request.Positions);
        var response = await Controller(true, true).DryRun(request, CancellationToken.None);
        var actual = Assert.IsType<RebalanceExecutionDryRunResult>(
            Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal(expected.SignalId, actual.SignalId);
        Assert.Equal(expected.ClientOrderId, actual.ClientOrderId);
        Assert.Equal("Prepared", actual.State);
        Assert.False(actual.MutationPerformed);
        Assert.False(actual.PersistencePerformed);
        Assert.False(actual.BrokerRequestSent);
    }

    [Fact]
    public async Task RiskRejectionRemainsBlockedWithoutClientOrderId()
    {
        var response = await Controller(true, true).DryRun(Request(false), CancellationToken.None);
        var actual = Assert.IsType<RebalanceExecutionDryRunResult>(
            Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal("BlockedByRisk", actual.State);
        Assert.Null(actual.ClientOrderId);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RequiresExplicitFeatureAndOperatorAuthentication(
        bool feature, bool authenticated)
    {
        Assert.IsType<NotFoundResult>(
            await Controller(feature, authenticated).DryRun(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task RebalancePlanWithoutIntentFailsClosed()
    {
        var request = Request();
        var response = await Controller(true, true).DryRun(
            request with { Plan = request.Plan with { OrderIntent = null } },
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(response);
    }
}
