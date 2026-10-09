using TradeOps.Application.Models;
using TradeOps.Application.Services.Backtesting;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Services;

public enum ResearchDecisionTimingMode
{
    ObservedRetrieval = 0,
    HistoricalPublishedAvailability = 1
}

public sealed record ResearchToRebalanceDemoRequest(
    IReadOnlyList<EarningsEvent> HistoricalEarningsEvents,
    IReadOnlyList<MarketDataBar> HistoricalDailyMarketBars,
    EarningsDecisionRuleSettings RuleSettings,
    EarningsTargetWeightPolicy TargetWeightPolicy,
    decimal InitialCash,
    PortfolioSnapshot CurrentPortfolio,
    decimal CurrentReferencePrice,
    BacktestExecutionCosts? ExecutionCosts = null,
    RebalanceConstraints? BacktestConstraints = null,
    RebalanceConstraints? CurrentRebalanceConstraints = null,
    string StrategyId = "sample-earnings-research-policy-v1",
    ResearchDecisionTimingMode DecisionTimingMode =
        ResearchDecisionTimingMode.ObservedRetrieval);

public sealed record ResearchToRebalanceDemoResult(
    InstrumentReference Instrument,
    string EventId,
    string FiscalPeriod,
    DateTimeOffset PublishedAt,
    EarningsAssessment Assessment,
    decimal TargetWeight,
    DateTimeOffset BacktestPeriodStart,
    DateTimeOffset BacktestPeriodEnd,
    int EventCount,
    decimal TotalReturn,
    decimal? Cagr,
    decimal MaxDrawdown,
    decimal? Sharpe,
    decimal? Sortino,
    decimal Turnover,
    decimal FinalEquity,
    decimal CurrentPortfolioWeight,
    decimal CurrentPositionQuantity,
    decimal ProposedTargetWeight,
    RebalancePlanStatus RebalanceStatus,
    OrderSide? RebalanceSide,
    decimal? RebalanceQuantity,
    decimal? RebalanceNotional,
    EarningsAssessmentResult LatestAssessment,
    ResearchDecision LatestDecision,
    BacktestRunResult Backtest,
    RebalancePlan CurrentRebalancePlan);

public sealed class ResearchToRebalanceDemoService
{
    private readonly EventDrivenBacktester _backtester;

    public ResearchToRebalanceDemoService(
        EventDrivenBacktester? backtester = null)
    {
        _backtester =
            backtester ??
            new EventDrivenBacktester();
    }

    public ResearchToRebalanceDemoResult Run(
        ResearchToRebalanceDemoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(
            request.HistoricalEarningsEvents);
        ArgumentNullException.ThrowIfNull(
            request.HistoricalDailyMarketBars);
        ArgumentNullException.ThrowIfNull(
            request.RuleSettings);
        ArgumentNullException.ThrowIfNull(
            request.TargetWeightPolicy);
        ArgumentNullException.ThrowIfNull(
            request.CurrentPortfolio);

        if (request.InitialCash <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Demo initial cash must be greater than zero.");
        }

        if (request.CurrentReferencePrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Current reference price must be greater than zero.");
        }

        var earningsEvents =
            request.HistoricalEarningsEvents
                .OrderBy(
                    item =>
                        item.PublishedAt)
                .ThenBy(
                    item =>
                        item.EventId,
                    StringComparer.Ordinal)
                .ToArray();

        if (earningsEvents.Length < 2)
        {
            throw new ArgumentException(
                "The demo requires at least two historical earnings events so each replayed event has a prior comparable.",
                nameof(request));
        }

        var replayItems =
            new List<BacktestReplayItem>(
                earningsEvents.Length - 1);

        EarningsAssessmentResult?
            latestAssessment = null;
        ResearchDecision?
            latestDecision = null;

        for (var index = 1;
             index < earningsEvents.Length;
             index++)
        {
            var prior =
                earningsEvents[index - 1];
            var current =
                earningsEvents[index];

            var assessment =
                DeterministicEarningsDecisionRule
                    .Assess(
                        current,
                        prior,
                        request.RuleSettings);

            var generatedAt =
                GetDeterministicGeneratedAt(
                    current,
                    request.DecisionTimingMode);

            var decision =
                DeterministicEarningsDecisionRule
                    .Evaluate(
                        current,
                        prior,
                        generatedAt,
                        request.TargetWeightPolicy,
                        request.StrategyId,
                        request.RuleSettings);

            replayItems.Add(
                new BacktestReplayItem(
                    current,
                    decision));

            latestAssessment =
                assessment;
            latestDecision =
                decision;
        }

        var finalAssessment =
            latestAssessment ??
            throw new InvalidOperationException(
                "Demo did not produce an earnings assessment.");
        var finalDecision =
            latestDecision ??
            throw new InvalidOperationException(
                "Demo did not produce a research decision.");

        var backtest =
            _backtester.Run(
                replayItems,
                request.HistoricalDailyMarketBars,
                request.InitialCash,
                request.CurrentPortfolio.BaseCurrency,
                request.ExecutionCosts,
                request.BacktestConstraints);

        var currentPlan =
            PortfolioRebalancePlanner.Plan(
                finalDecision,
                request.CurrentPortfolio,
                request.CurrentReferencePrice,
                request.CurrentRebalanceConstraints);

        if (currentPlan.TargetPosition is null ||
            !currentPlan.CurrentQuantity.HasValue ||
            !currentPlan.CurrentNotional.HasValue)
        {
            throw new InvalidOperationException(
                "The current portfolio could not be translated into a calculated rebalance plan for the demo.");
        }

        var equityCurve =
            backtest.EquityCurve;

        if (equityCurve.Count == 0)
        {
            throw new InvalidOperationException(
                "Backtest did not produce an equity curve.");
        }

        var metrics =
            backtest.Metrics;
        var orderIntent =
            currentPlan.OrderIntent;
        var currentWeight =
            currentPlan.CurrentNotional.Value /
            request.CurrentPortfolio.NetAssetValue;

        return new ResearchToRebalanceDemoResult(
            finalDecision.Instrument,
            earningsEvents[^1].EventId,
            earningsEvents[^1].FiscalPeriod,
            earningsEvents[^1].PublishedAt,
            finalAssessment.Assessment,
            finalDecision.TargetWeight!.Value,
            equityCurve[0].AsOf,
            equityCurve[^1].AsOf,
            metrics.EventCount,
            metrics.TotalReturn,
            metrics.Cagr,
            metrics.MaxDrawdown,
            metrics.Sharpe,
            metrics.Sortino,
            metrics.Turnover,
            backtest.FinalPortfolio.NetAssetValue,
            currentWeight,
            currentPlan.CurrentQuantity.Value,
            currentPlan.TargetPosition.TargetWeight,
            currentPlan.Status,
            orderIntent?.Side,
            orderIntent?.Quantity,
            orderIntent?.EstimatedNotional,
            finalAssessment,
            finalDecision,
            backtest,
            currentPlan);
    }

    private static DateTimeOffset
        GetDeterministicGeneratedAt(
            EarningsEvent earningsEvent,
            ResearchDecisionTimingMode timingMode)
    {
        var publishedAt =
            earningsEvent.PublishedAt
                .ToUniversalTime();

        return timingMode switch
        {
            ResearchDecisionTimingMode
                .ObservedRetrieval =>
                LaterOf(
                    publishedAt,
                    earningsEvent.Provenance
                        .RetrievedAt
                        .ToUniversalTime()),

            ResearchDecisionTimingMode
                .HistoricalPublishedAvailability =>
                publishedAt,

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(timingMode),
                    timingMode,
                    "Unsupported research-decision timing mode.")
        };
    }

    private static DateTimeOffset LaterOf(
        DateTimeOffset left,
        DateTimeOffset right) =>
        right > left
            ? right
            : left;
}
