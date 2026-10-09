using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class RebalanceExecutionDryRunPreviewTests
{
    private static readonly DateTimeOffset PlannedAt =
        new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static RebalancePlan Plan()
    {
        var instrument = new InstrumentReference("SAMP", AssetClass.Stock, "USD");
        var intent = new RebalanceOrderIntent("decision-test", instrument, OrderSide.Buy, 30m, 100m, 3000m);
        return new RebalancePlan("decision-test", "strategy-test", PlannedAt, null,
            0m, 0m, 30m, 3000m, RebalancePlanStatus.Ready, intent,
            Array.Empty<RebalanceConstraintViolation>());
    }

    private static RiskSettings Settings() => new()
    {
        MaxOrderSize = 100m, MaxPositionSize = 100m, MaxDailyLoss = 500m,
        MaxOpenPositions = 5,
        AllowedSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SAMP" }
    };

    private static RiskControlSnapshot Controls(bool enabled) => new(
        enabled, false, null, "USD", 0m, 0m, 0m,
        Array.Empty<UnconvertedFee>(), 0, PlannedAt);

    [Fact]
    public async Task AllowedPreviewIsStableAndContainsNoMutation()
    {
        var first = await RebalanceExecutionDryRunPreview.RunAsync(
            Plan(), Settings(), Controls(true), Array.Empty<Position>());
        var second = await RebalanceExecutionDryRunPreview.RunAsync(
            Plan(), Settings(), Controls(true), Array.Empty<Position>());
        Assert.Equal(first, second);
        Assert.Equal("Prepared", first.State);
        Assert.Equal("dryRun", first.Mode);
        Assert.Equal(new ClientOrderIdGenerator().Generate(first.SignalId), first.ClientOrderId);
        Assert.True(first.RiskAllowed);
        Assert.Equal(OrderType.Market, first.OrderType);
        Assert.Equal(30m, first.Quantity);
        Assert.Equal(3000m, first.EstimatedNotional);
        Assert.False(first.MutationPerformed);
        Assert.False(first.BrokerRequestSent);
        Assert.False(first.PersistencePerformed);
    }

    [Fact]
    public async Task RejectionCannotPrepareClientOrderIdentity()
    {
        var result = await RebalanceExecutionDryRunPreview.RunAsync(
            Plan(), Settings(), Controls(false), Array.Empty<Position>());
        Assert.Equal("BlockedByRisk", result.State);
        Assert.Null(result.ClientOrderId);
        Assert.False(result.RiskAllowed);
        Assert.NotEmpty(result.RiskReasons);
    }

    [Fact]
    public async Task InvalidPlanFailsClosed()
    {
        var plan = Plan() with { Status = RebalancePlanStatus.Blocked };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RebalanceExecutionDryRunPreview.RunAsync(
                plan, Settings(), Controls(true), Array.Empty<Position>()));
    }

    [Fact]
    public void ExistingSignalProjectionMatchesSharedApplication()
    {
        var plan = Plan();
        var shared = RebalancePreviewSignalProjector.Project(plan);
        var legacy = TradeOps.TranscriptResearchDemo.RebalanceRiskPreviewSignalProjector.Project(plan);
        Assert.Equal(shared.Id, legacy.Id);
        Assert.Equal(shared.Symbol, legacy.Symbol);
        Assert.Equal(shared.RequestedQuantity, legacy.RequestedQuantity);
    }
}
