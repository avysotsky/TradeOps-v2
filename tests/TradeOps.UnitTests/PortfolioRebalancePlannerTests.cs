using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class PortfolioRebalancePlannerTests
{
    private static readonly DateTimeOffset SnapshotTime =
        new(
            2026,
            10,
            6,
            18,
            0,
            0,
            TimeSpan.Zero);

    [Fact]
    public void Plan_OnePointEightPercentToFourPercent_CalculatesExpectedDelta()
    {
        var plan =
            PortfolioRebalancePlanner.Plan(
                CreateDecision(0.04m),
                new PortfolioSnapshot(
                    "USD",
                    NetAssetValue: 100_000m,
                    Cash: 20_000m,
                    Positions:
                    [
                        new PortfolioPosition(
                            Apple(),
                            18m)
                    ],
                    AsOf: SnapshotTime),
                referencePrice: 100m,
                new RebalanceConstraints(
                    MaxTargetWeight: 0.10m,
                    MinimumTradeNotional: 100m));

        Assert.Equal(
            RebalancePlanStatus.Ready,
            plan.Status);
        Assert.Equal(
            4_000m,
            plan.TargetPosition!.TargetNotional);
        Assert.Equal(
            40m,
            plan.TargetPosition.TargetQuantity);
        Assert.Equal(
            18m,
            plan.CurrentQuantity);
        Assert.Equal(
            1_800m,
            plan.CurrentNotional);
        Assert.Equal(
            22m,
            plan.DeltaQuantity);
        Assert.Equal(
            2_200m,
            plan.DeltaNotional);

        var intent =
            Assert.IsType<RebalanceOrderIntent>(
                plan.OrderIntent);

        Assert.Equal(
            OrderSide.Buy,
            intent.Side);
        Assert.Equal(
            22m,
            intent.Quantity);
        Assert.Equal(
            2_200m,
            intent.EstimatedNotional);
        Assert.True(
            intent.RequiresRiskApproval);
    }

    [Fact]
    public void Plan_NewPosition_CreatesBuyIntent()
    {
        var plan =
            PortfolioRebalancePlanner.Plan(
                CreateDecision(0.10m),
                CreatePortfolio(
                    cash: 20_000m),
                referencePrice: 200m,
                new RebalanceConstraints(
                    MaxTargetWeight: 0.20m,
                    MinimumTradeNotional: 100m));

        Assert.Equal(
            RebalancePlanStatus.Ready,
            plan.Status);
        Assert.Equal(
            10_000m,
            plan.TargetPosition!.TargetNotional);
        Assert.Equal(
            50m,
            plan.TargetPosition.TargetQuantity);
        Assert.Equal(
            50m,
            plan.DeltaQuantity);

        var intent =
            Assert.IsType<RebalanceOrderIntent>(
                plan.OrderIntent);

        Assert.Equal(
            OrderSide.Buy,
            intent.Side);
        Assert.Equal(
            50m,
            intent.Quantity);
        Assert.Equal(
            10_000m,
            intent.EstimatedNotional);
        Assert.True(
            intent.RequiresRiskApproval);
    }

    [Fact]
    public void Plan_AboveTarget_CreatesSellIntent()
    {
        var plan =
            PortfolioRebalancePlanner.Plan(
                CreateDecision(0.10m),
                CreatePortfolio(
                    cash: 5_000m,
                    positions:
                    [
                        new PortfolioPosition(
                            Apple(),
                            80m)
                    ]),
                referencePrice: 200m);

        Assert.Equal(
            RebalancePlanStatus.Ready,
            plan.Status);
        Assert.Equal(
            -30m,
            plan.DeltaQuantity);
        Assert.Equal(
            -6_000m,
            plan.DeltaNotional);

        var intent =
            Assert.IsType<RebalanceOrderIntent>(
                plan.OrderIntent);

        Assert.Equal(
            OrderSide.Sell,
            intent.Side);
        Assert.Equal(
            30m,
            intent.Quantity);
    }

    [Fact]
    public void Plan_TargetZero_ExitsLongPosition()
    {
        var plan =
            PortfolioRebalancePlanner.Plan(
                CreateDecision(0m),
                CreatePortfolio(
                    cash: 5_000m,
                    positions:
                    [
                        new PortfolioPosition(
                            Apple(),
                            25m)
                    ]),
                referencePrice: 200m);

        Assert.Equal(
            RebalancePlanStatus.Ready,
            plan.Status);
        Assert.Equal(
            0m,
            plan.TargetPosition!.TargetQuantity);

        var intent =
            Assert.IsType<RebalanceOrderIntent>(
                plan.OrderIntent);

        Assert.Equal(
            OrderSide.Sell,
            intent.Side);
        Assert.Equal(
            25m,
            intent.Quantity);
    }

    [Fact]
    public void Plan_AtTarget_ReturnsNoAction()
    {
        var plan =
            PortfolioRebalancePlanner.Plan(
                CreateDecision(0.10m),
                CreatePortfolio(
                    cash: 10_000m,
                    positions:
                    [
                        new PortfolioPosition(
                            Apple(),
                            50m)
                    ]),
                referencePrice: 200m);

        Assert.Equal(
            RebalancePlanStatus.NoAction,
            plan.Status);
        Assert.Null(
            plan.OrderIntent);
        Assert.Empty(
            plan.ConstraintViolations);
    }

    [Fact]
    public void Plan_QuantityWithinTolerance_ReturnsNoAction()
    {
        var plan =
            PortfolioRebalancePlanner.Plan(
                CreateDecision(0.10m),
                CreatePortfolio(
                    cash: 10_000m,
                    positions:
                    [
                        new PortfolioPosition(
                            Apple(),
                            49.999m)
                    ]),
                referencePrice: 200m,
                new RebalanceConstraints(
                    QuantityTolerance: 0.01m));

        Assert.Equal(
            RebalancePlanStatus.NoAction,
            plan.Status);
        Assert.Null(
            plan.OrderIntent);
        Assert.Contains(
            plan.ConstraintViolations,
            item =>
                item.Code ==
                "QuantityTolerance");
    }

    [Fact]
    public void Plan_BelowMinimumTrade_ReturnsNoAction()
    {
        var plan =
            PortfolioRebalancePlanner.Plan(
                CreateDecision(0.10m),
                CreatePortfolio(
                    cash: 10_000m,
                    positions:
                    [
                        new PortfolioPosition(
                            Apple(),
                            49.9m)
                    ]),
                referencePrice: 200m,
                new RebalanceConstraints(
                    MinimumTradeNotional: 50m));

        Assert.Equal(
            RebalancePlanStatus.NoAction,
            plan.Status);
        Assert.Null(
            plan.OrderIntent);
        Assert.Contains(
            plan.ConstraintViolations,
            item =>
                item.Code ==
                "MinimumTradeNotional");
    }

    [Fact]
    public void Plan_InsufficientCash_BlocksBuy()
    {
        var plan =
            PortfolioRebalancePlanner.Plan(
                CreateDecision(0.10m),
                CreatePortfolio(
                    cash: 10_000m),
                referencePrice: 200m,
                new RebalanceConstraints(
                    MinimumCashReserve: 1_000m));

        Assert.Equal(
            RebalancePlanStatus.Blocked,
            plan.Status);
        Assert.Null(
            plan.OrderIntent);
        Assert.Contains(
            plan.ConstraintViolations,
            item =>
                item.Code ==
                "InsufficientCash");
    }

    [Fact]
    public void Plan_TargetAboveMaximumWeight_IsBlocked()
    {
        var plan =
            PortfolioRebalancePlanner.Plan(
                CreateDecision(0.25m),
                CreatePortfolio(
                    cash: 100_000m),
                referencePrice: 200m,
                new RebalanceConstraints(
                    MaxTargetWeight: 0.20m));

        Assert.Equal(
            RebalancePlanStatus.Blocked,
            plan.Status);
        Assert.Contains(
            plan.ConstraintViolations,
            item =>
                item.Code ==
                "MaxTargetWeight");
    }

    [Fact]
    public void Plan_NegativeTarget_IsBlockedByLongOnlyConstraint()
    {
        var plan =
            PortfolioRebalancePlanner.Plan(
                CreateDecision(-0.05m),
                CreatePortfolio(
                    cash: 100_000m),
                referencePrice: 200m);

        Assert.Equal(
            RebalancePlanStatus.Blocked,
            plan.Status);
        Assert.Contains(
            plan.ConstraintViolations,
            item =>
                item.Code ==
                "LongOnly");
    }

    [Fact]
    public void Plan_ExpiredDecision_IsBlockedAtSnapshotTime()
    {
        var plan =
            PortfolioRebalancePlanner.Plan(
                CreateDecision(
                    0.10m,
                    validUntil:
                        SnapshotTime.AddMinutes(-1)),
                CreatePortfolio(
                    cash: 100_000m),
                referencePrice: 200m);

        Assert.Equal(
            RebalancePlanStatus.Blocked,
            plan.Status);
        Assert.Contains(
            plan.ConstraintViolations,
            item =>
                item.Code ==
                "DecisionExpired");
    }

    [Fact]
    public void Plan_DecisionGeneratedAfterSnapshot_IsBlocked()
    {
        var decision =
            CreateDecision(0.10m) with
            {
                GeneratedAt =
                    SnapshotTime.AddMinutes(1)
            };

        var plan =
            PortfolioRebalancePlanner.Plan(
                decision,
                CreatePortfolio(
                    cash: 100_000m),
                referencePrice: 200m);

        Assert.Equal(
            RebalancePlanStatus.Blocked,
            plan.Status);
        Assert.Contains(
            plan.ConstraintViolations,
            item =>
                item.Code ==
                "DecisionNotYetAvailable");
    }

    [Fact]
    public void Plan_DecisionOlderThanMaximumAge_IsBlocked()
    {
        var decision =
            CreateDecision(0.10m) with
            {
                GeneratedAt =
                    SnapshotTime.AddDays(-10),
                ValidUntil = null
            };

        var plan =
            PortfolioRebalancePlanner.Plan(
                decision,
                CreatePortfolio(
                    cash: 100_000m),
                referencePrice: 200m,
                new RebalanceConstraints(
                    MaximumDecisionAge:
                        TimeSpan.FromDays(7)));

        Assert.Equal(
            RebalancePlanStatus.Blocked,
            plan.Status);
        Assert.Contains(
            plan.ConstraintViolations,
            item =>
                item.Code ==
                "DecisionStale");
    }

    [Fact]
    public void Plan_MissingPrice_IsBlocked()
    {
        var plan =
            PortfolioRebalancePlanner.Plan(
                CreateDecision(0.10m),
                CreatePortfolio(
                    cash: 100_000m),
                referencePrice: null);

        Assert.Equal(
            RebalancePlanStatus.Blocked,
            plan.Status);
        Assert.Null(
            plan.TargetPosition);
        Assert.Contains(
            plan.ConstraintViolations,
            item =>
                item.Code ==
                "MissingPrice");
    }

    [Fact]
    public void Plan_InvalidResearchDecision_IsBlocked()
    {
        var decision =
            CreateDecision(0.10m) with
            {
                TargetWeight = null
            };

        var plan =
            PortfolioRebalancePlanner.Plan(
                decision,
                CreatePortfolio(
                    cash: 100_000m),
                referencePrice: 200m);

        Assert.Equal(
            RebalancePlanStatus.Blocked,
            plan.Status);
        Assert.Null(
            plan.TargetPosition);
        Assert.Contains(
            plan.ConstraintViolations,
            item =>
                item.Code ==
                "InvalidResearchDecision");
    }

    [Fact]
    public void Plan_NonStockDecision_IsBlocked()
    {
        var decision =
            CreateDecision(0.10m) with
            {
                Instrument =
                    new InstrumentReference(
                        "BTC",
                        AssetClass.Crypto,
                        "USD")
            };

        var plan =
            PortfolioRebalancePlanner.Plan(
                decision,
                CreatePortfolio(
                    cash: 100_000m),
                referencePrice: 60_000m);

        Assert.Equal(
            RebalancePlanStatus.Blocked,
            plan.Status);
        Assert.Contains(
            plan.ConstraintViolations,
            item =>
                item.Code ==
                "UnsupportedAssetClass");
    }

    [Fact]
    public void Plan_NonTargetWeightAction_IsBlocked()
    {
        var decision =
            CreateDecision(0.10m) with
            {
                Action =
                    ResearchDecisionAction.Buy,
                TargetWeight = null
            };

        var plan =
            PortfolioRebalancePlanner.Plan(
                decision,
                CreatePortfolio(
                    cash: 100_000m),
                referencePrice: 200m);

        Assert.Equal(
            RebalancePlanStatus.Blocked,
            plan.Status);
        Assert.Contains(
            plan.ConstraintViolations,
            item =>
                item.Code ==
                "UnsupportedAction");
    }

    private static ResearchDecision CreateDecision(
        decimal targetWeight,
        DateTimeOffset? validUntil = null) =>
        new(
            "decision-1",
            "earnings-quality-v1",
            Apple(),
            ResearchDecisionAction.SetTargetWeight,
            SnapshotTime.AddHours(-1),
            TargetWeight:
                targetWeight,
            Confidence: 0.80m,
            SourceEventId: "event-1",
            ValidUntil:
                validUntil ??
                SnapshotTime.AddDays(1));

    private static PortfolioSnapshot CreatePortfolio(
        decimal cash,
        IReadOnlyList<PortfolioPosition>? positions = null) =>
        new(
            "USD",
            NetAssetValue: 100_000m,
            Cash: cash,
            Positions:
                positions ??
                Array.Empty<PortfolioPosition>(),
            AsOf: SnapshotTime);

    private static InstrumentReference Apple() =>
        new(
            "AAPL",
            AssetClass.Stock,
            "USD",
            "265598",
            "SMART");
}
