using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class ResearchToRebalanceDemoTests
{
    private static readonly InstrumentReference Instrument =
        new(
            "SAMP",
            AssetClass.Stock,
            "USD",
            VenueInstrumentId: "sample-stock-1",
            Exchange: "SAMPLE");

    private static readonly EarningsDecisionRuleSettings RuleSettings =
        new(
            RevenueGrowthThreshold: 0.05m,
            DilutedEpsGrowthThreshold: 0.05m,
            OperatingMarginDeltaThreshold: 0.01m,
            MinimumDirectionalSignals: 2);

    private static readonly EarningsTargetWeightPolicy TargetWeightPolicy =
        new(
            PositiveTargetWeight: 0.40m,
            NeutralTargetWeight: 0.20m,
            NegativeTargetWeight: 0m);

    [Fact]
    public void Run_end_to_end_demo_reuses_research_backtest_and_rebalance_pipeline()
    {
        var request =
            CreateRequest();

        var result =
            new ResearchToRebalanceDemoService()
                .Run(request);

        Assert.Equal(
            Instrument,
            result.Instrument);
        Assert.Equal(
            "sample-earnings-2026q1",
            result.EventId);
        Assert.Equal(
            "FY2026-Q1",
            result.FiscalPeriod);
        Assert.Equal(
            EarningsAssessment.Positive,
            result.Assessment);
        Assert.Equal(
            0.40m,
            result.TargetWeight);

        Assert.Equal(
            ResearchDecisionAction.SetTargetWeight,
            result.LatestDecision.Action);
        Assert.Equal(
            result.EventId,
            result.LatestDecision.SourceEventId);
        Assert.Equal(
            request.HistoricalEarningsEvents[^1]
                .Provenance.RetrievedAt,
            result.LatestDecision.GeneratedAt);

        Assert.Equal(
            3,
            result.EventCount);
        Assert.Equal(
            3,
            result.Backtest.Plans.Count);
        Assert.Equal(
            3,
            result.Backtest.Fills.Count);
        Assert.Equal(
            result.EventCount,
            result.Backtest.Metrics.EventCount);
        Assert.True(
            result.BacktestPeriodEnd >
            result.BacktestPeriodStart);
        Assert.Equal(
            10_360m,
            result.FinalEquity);
        Assert.Equal(
            0.036m,
            result.TotalReturn);
        Assert.True(
            result.Turnover >
            0m);

        Assert.Equal(
            10m,
            result.CurrentPositionQuantity);
        Assert.Equal(
            0.10m,
            result.CurrentPortfolioWeight);
        Assert.Equal(
            0.40m,
            result.ProposedTargetWeight);
        Assert.Equal(
            RebalancePlanStatus.Ready,
            result.RebalanceStatus);
        Assert.Equal(
            OrderSide.Buy,
            result.RebalanceSide);
        Assert.Equal(
            30m,
            result.RebalanceQuantity);
        Assert.Equal(
            3_000m,
            result.RebalanceNotional);

        Assert.NotNull(
            result.CurrentRebalancePlan.OrderIntent);
        Assert.True(
            result.CurrentRebalancePlan
                .OrderIntent!
                .RequiresRiskApproval);
    }

    [Fact]
    public void Run_default_timing_mode_preserves_observed_retrieval_behavior()
    {
        var request =
            CreateRequest();

        var result =
            new ResearchToRebalanceDemoService()
                .Run(request);
        var latestEvent =
            request.HistoricalEarningsEvents[^1];

        Assert.Equal(
            latestEvent.Provenance.RetrievedAt,
            result.LatestDecision.GeneratedAt);
    }

    [Fact]
    public void Run_historical_timing_mode_generates_at_published_availability()
    {
        var request =
            CreateRequest(
                ResearchDecisionTimingMode
                    .HistoricalPublishedAvailability);
        var latestEvent =
            request.HistoricalEarningsEvents[^1];
        var actualRetrievedAt =
            latestEvent.Provenance.RetrievedAt;

        var result =
            new ResearchToRebalanceDemoService()
                .Run(request);

        Assert.Equal(
            latestEvent.PublishedAt,
            result.LatestDecision.GeneratedAt);
        Assert.True(
            actualRetrievedAt >
            result.LatestDecision.GeneratedAt);
        Assert.Equal(
            actualRetrievedAt,
            latestEvent.Provenance.RetrievedAt);
    }

    [Fact]
    public void Run_same_fixture_is_deterministic()
    {
        var request =
            CreateRequest();
        var sut =
            new ResearchToRebalanceDemoService();

        var first =
            sut.Run(request);
        var second =
            sut.Run(request);

        Assert.Equal(
            first.LatestDecision.DecisionId,
            second.LatestDecision.DecisionId);
        Assert.Equal(
            first.LatestDecision.Action,
            second.LatestDecision.Action);
        Assert.Equal(
            first.LatestDecision.TargetWeight,
            second.LatestDecision.TargetWeight);
        Assert.Equal(
            first.LatestDecision.GeneratedAt,
            second.LatestDecision.GeneratedAt);
        Assert.Equal(
            first.LatestAssessment,
            second.LatestAssessment);
        Assert.Equal(
            first.Backtest.Fills.ToArray(),
            second.Backtest.Fills.ToArray());
        Assert.Equal(
            first.Backtest.EquityCurve.ToArray(),
            second.Backtest.EquityCurve.ToArray());
        Assert.Equal(
            first.Backtest.Metrics,
            second.Backtest.Metrics);
        Assert.Equal(
            first.CurrentRebalancePlan.Status,
            second.CurrentRebalancePlan.Status);
        Assert.Equal(
            first.CurrentRebalancePlan.TargetPosition,
            second.CurrentRebalancePlan.TargetPosition);
        Assert.Equal(
            first.CurrentRebalancePlan.CurrentQuantity,
            second.CurrentRebalancePlan.CurrentQuantity);
        Assert.Equal(
            first.CurrentRebalancePlan.DeltaQuantity,
            second.CurrentRebalancePlan.DeltaQuantity);
        Assert.Equal(
            first.CurrentRebalancePlan.OrderIntent,
            second.CurrentRebalancePlan.OrderIntent);
    }

    private static ResearchToRebalanceDemoRequest
        CreateRequest(
            ResearchDecisionTimingMode timingMode =
                ResearchDecisionTimingMode
                    .ObservedRetrieval)
    {
        var earnings =
            new[]
            {
                Earnings(
                    "sample-earnings-2025q2",
                    "FY2025-Q2",
                    Utc(
                        2025,
                        10,
                        1,
                        21,
                        30),
                    revenue: 100m,
                    dilutedEps: 2.00m,
                    operatingMargin: 0.20m),
                Earnings(
                    "sample-earnings-2025q3",
                    "FY2025-Q3",
                    Utc(
                        2026,
                        1,
                        2,
                        21,
                        30),
                    revenue: 110m,
                    dilutedEps: 2.20m,
                    operatingMargin: 0.22m),
                Earnings(
                    "sample-earnings-2025q4",
                    "FY2025-Q4",
                    Utc(
                        2026,
                        1,
                        6,
                        21,
                        30),
                    revenue: 100m,
                    dilutedEps: 2.00m,
                    operatingMargin: 0.20m),
                Earnings(
                    "sample-earnings-2026q1",
                    "FY2026-Q1",
                    Utc(
                        2026,
                        1,
                        8,
                        21,
                        30),
                    revenue: 120m,
                    dilutedEps: 2.30m,
                    operatingMargin: 0.23m)
            };

        var bars =
            new[]
            {
                Bar(
                    2026,
                    1,
                    2,
                    99m,
                    100m),
                Bar(
                    2026,
                    1,
                    5,
                    100m,
                    101m),
                Bar(
                    2026,
                    1,
                    6,
                    102m,
                    99m),
                Bar(
                    2026,
                    1,
                    7,
                    105m,
                    103m),
                Bar(
                    2026,
                    1,
                    8,
                    104m,
                    104m),
                Bar(
                    2026,
                    1,
                    9,
                    102m,
                    100m),
                Bar(
                    2026,
                    1,
                    12,
                    101m,
                    106m)
            };

        var currentPortfolio =
            new PortfolioSnapshot(
                "USD",
                NetAssetValue: 10_000m,
                Cash: 9_000m,
                Positions:
                    new[]
                    {
                        new PortfolioPosition(
                            Instrument,
                            Quantity: 10m)
                    },
                AsOf:
                    Utc(
                        2026,
                        1,
                        12,
                        22,
                        0));

        return new ResearchToRebalanceDemoRequest(
            earnings,
            bars,
            RuleSettings,
            TargetWeightPolicy,
            InitialCash: 10_000m,
            currentPortfolio,
            CurrentReferencePrice: 100m,
            CurrentRebalanceConstraints:
                new RebalanceConstraints(
                    MaxTargetWeight: 0.50m),
            DecisionTimingMode:
                timingMode);
    }

    private static EarningsEvent Earnings(
        string eventId,
        string fiscalPeriod,
        DateTimeOffset publishedAt,
        decimal revenue,
        decimal dilutedEps,
        decimal operatingMargin) =>
        new(
            eventId,
            Instrument,
            publishedAt,
            fiscalPeriod,
            new EarningsSnapshot(
                Revenue: revenue,
                DilutedEps: dilutedEps,
                OperatingMargin: operatingMargin),
            new ResearchSourceProvenance(
                "sample-research-provider",
                new Uri(
                    $"https://research.example.test/{eventId}"),
                publishedAt.AddMinutes(-2),
                publishedAt.AddMinutes(1),
                "deterministic-fixture",
                SourceDocumentId:
                    $"document-{eventId}",
                IssuerId:
                    "sample-issuer-1"));

    private static MarketDataBar Bar(
        int year,
        int month,
        int day,
        decimal open,
        decimal close)
    {
        var high =
            Math.Max(
                open,
                close) +
            1m;
        var low =
            Math.Min(
                open,
                close) -
            1m;

        return new MarketDataBar(
            Instrument,
            MarketDataBarPeriod.Daily,
            Utc(
                year,
                month,
                day,
                14,
                30),
            Utc(
                year,
                month,
                day,
                21,
                0),
            open,
            high,
            low,
            close);
    }

    private static DateTimeOffset Utc(
        int year,
        int month,
        int day,
        int hour,
        int minute) =>
        new(
            year,
            month,
            day,
            hour,
            minute,
            0,
            TimeSpan.Zero);
}
