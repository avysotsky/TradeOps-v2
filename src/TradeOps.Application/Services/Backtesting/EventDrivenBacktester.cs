using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Services.Backtesting;

public sealed class EventDrivenBacktester
{
    private const decimal CashTolerance = 0.00000001m;

    private readonly IBacktestExecutionPricePolicy
        _executionPricePolicy;

    public EventDrivenBacktester(
        IBacktestExecutionPricePolicy?
            executionPricePolicy = null)
    {
        _executionPricePolicy =
            executionPricePolicy ??
            new NextBarOpenExecutionPricePolicy();
    }

    public BacktestRunResult Run(
        IEnumerable<BacktestReplayItem> replayItems,
        IEnumerable<MarketDataBar> marketData,
        decimal initialCash,
        string baseCurrency = "USD",
        BacktestExecutionCosts? executionCosts = null,
        RebalanceConstraints? constraints = null)
    {
        ArgumentNullException.ThrowIfNull(replayItems);
        ArgumentNullException.ThrowIfNull(marketData);

        if (initialCash <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialCash),
                "Initial cash must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(baseCurrency))
        {
            throw new ArgumentException(
                "Base currency is required.",
                nameof(baseCurrency));
        }

        executionCosts ??=
            new BacktestExecutionCosts();

        constraints ??=
            new RebalanceConstraints();

        ValidateExecutionCosts(
            executionCosts);

        var items =
            replayItems.ToArray();

        var bars =
            marketData
                .OrderBy(bar => bar.OpenTime)
                .ThenBy(
                    bar =>
                        bar.Instrument.Symbol,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        if (items.Length == 0)
        {
            throw new ArgumentException(
                "At least one replay item is required.",
                nameof(replayItems));
        }

        if (bars.Length == 0)
        {
            throw new ArgumentException(
                "At least one market-data bar is required.",
                nameof(marketData));
        }

        foreach (var bar in bars)
        {
            ValidateBar(bar);
        }

        var instrument =
            items[0].Decision.Instrument;

        ValidateSingleInstrumentSlice(
            items,
            bars,
            instrument);

        ValidateDailyBarSeries(
            bars);

        var schedules =
            BuildSchedules(
                items,
                bars);

        var plans =
            new List<RebalancePlan>();
        var fills =
            new List<BacktestFill>();
        var equityCurve =
            new List<BacktestEquityPoint>
            {
                new(
                    bars[0].OpenTime,
                    initialCash,
                    0m)
            };

        var cash =
            initialCash;
        var quantity =
            0m;
        var totalTurnoverNotional =
            0m;

        var clock =
            new DeterministicEventClock(
                bars.SelectMany(
                    bar =>
                        new[]
                        {
                            bar.OpenTime,
                            bar.CloseTime
                        }));

        while (clock.MoveNext())
        {
            var now =
                clock.Current!.Value;

            foreach (
                var bar
                in bars.Where(
                    candidate =>
                        candidate.OpenTime
                            .ToUniversalTime() ==
                        now))
            {
                var matchingSchedules =
                    schedules
                        .Where(
                            schedule =>
                                schedule.ExecutionBar
                                    .OpenTime
                                    .ToUniversalTime() ==
                                now)
                        .OrderBy(
                            schedule =>
                                schedule.Item
                                    .Decision
                                    .GeneratedAt)
                        .ThenBy(
                            schedule =>
                                schedule.Item
                                    .Decision
                                    .DecisionId,
                            StringComparer.Ordinal)
                        .ToArray();

                foreach (
                    var schedule
                    in matchingSchedules)
                {
                    var snapshot =
                        BuildSnapshot(
                            baseCurrency,
                            instrument,
                            cash,
                            quantity,
                            bar.Open,
                            bar.OpenTime);

                    var plan =
                        PortfolioRebalancePlanner.Plan(
                            schedule.Item.Decision,
                            snapshot,
                            bar.Open,
                            constraints);

                    plans.Add(plan);

                    if (plan.Status !=
                            RebalancePlanStatus.Ready ||
                        plan.OrderIntent is null)
                    {
                        continue;
                    }

                    var fill =
                        Execute(
                            plan.OrderIntent,
                            bar,
                            executionCosts,
                            ref cash,
                            ref quantity);

                    fills.Add(fill);
                    totalTurnoverNotional +=
                        fill.GrossNotional;
                }
            }

            foreach (
                var bar
                in bars.Where(
                    candidate =>
                        candidate.CloseTime
                            .ToUniversalTime() ==
                        now))
            {
                var equity =
                    cash +
                    quantity *
                    bar.Close;
                var exposure =
                    Math.Abs(
                        quantity *
                        bar.Close);

                equityCurve.Add(
                    new BacktestEquityPoint(
                        bar.CloseTime,
                        equity,
                        exposure));
            }
        }

        var lastBar =
            bars[^1];

        var finalPortfolio =
            BuildSnapshot(
                baseCurrency,
                instrument,
                cash,
                quantity,
                lastBar.Close,
                lastBar.CloseTime);

        var metrics =
            BacktestPerformanceCalculator.Calculate(
                equityCurve,
                items.Length,
                totalTurnoverNotional,
                initialCash);

        return new BacktestRunResult(
            plans,
            fills,
            equityCurve,
            finalPortfolio,
            metrics);
    }

    private IReadOnlyList<ScheduledReplay>
        BuildSchedules(
            IReadOnlyList<BacktestReplayItem> items,
            IReadOnlyList<MarketDataBar> bars)
    {
        var schedules =
            new List<ScheduledReplay>(
                items.Count);

        foreach (var item in items)
        {
            ValidateAvailability(
                item);

            var availableAt =
                item.Decision.GeneratedAt >
                item.EarningsEvent.PublishedAt
                    ? item.Decision.GeneratedAt
                    : item.EarningsEvent.PublishedAt;

            var executionBar =
                _executionPricePolicy
                    .SelectExecutionBar(
                        bars,
                        item.Decision.Instrument,
                        availableAt);

            if (executionBar is null)
            {
                throw new InvalidOperationException(
                    $"No execution bar is available at or after {availableAt:O} for decision '{item.Decision.DecisionId}'.");
            }

            schedules.Add(
                new ScheduledReplay(
                    item,
                    executionBar));
        }

        return schedules;
    }

    private BacktestFill Execute(
        RebalanceOrderIntent intent,
        MarketDataBar bar,
        BacktestExecutionCosts costs,
        ref decimal cash,
        ref decimal quantity)
    {
        var executionPrice =
            _executionPricePolicy
                .ApplySlippage(
                    bar.Open,
                    intent.Side,
                    costs.SlippageBasisPoints);

        var grossNotional =
            intent.Quantity *
            executionPrice;
        var slippageCost =
            Math.Abs(
                executionPrice -
                bar.Open) *
            intent.Quantity;

        if (intent.Side ==
            OrderSide.Buy)
        {
            var cashRequired =
                grossNotional +
                costs.CommissionPerOrder;

            if (cashRequired >
                cash +
                CashTolerance)
            {
                throw new InvalidOperationException(
                    $"Planner-sized buy for decision '{intent.DecisionId}' is not affordable after configured backtest execution costs.");
            }

            cash -= cashRequired;
            quantity +=
                intent.Quantity;
        }
        else
        {
            if (intent.Quantity >
                quantity +
                CashTolerance)
            {
                throw new InvalidOperationException(
                    $"Planner-sized sell for decision '{intent.DecisionId}' exceeds simulated long-only position.");
            }

            cash +=
                grossNotional -
                costs.CommissionPerOrder;
            quantity -=
                intent.Quantity;

            if (Math.Abs(quantity) <=
                CashTolerance)
            {
                quantity = 0m;
            }
        }

        return new BacktestFill(
            intent.DecisionId,
            intent.Instrument,
            intent.Side,
            bar.OpenTime,
            intent.Quantity,
            bar.Open,
            executionPrice,
            grossNotional,
            costs.CommissionPerOrder,
            slippageCost);
    }

    private static PortfolioSnapshot
        BuildSnapshot(
            string baseCurrency,
            InstrumentReference instrument,
            decimal cash,
            decimal quantity,
            decimal markPrice,
            DateTimeOffset asOf)
    {
        var positions =
            quantity == 0m
                ? Array.Empty<PortfolioPosition>()
                : new[]
                {
                    new PortfolioPosition(
                        instrument,
                        quantity)
                };

        return new PortfolioSnapshot(
            baseCurrency.Trim().ToUpperInvariant(),
            cash +
            quantity *
            markPrice,
            cash,
            positions,
            asOf);
    }

    private static void ValidateAvailability(
        BacktestReplayItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(
            item.EarningsEvent);
        ArgumentNullException.ThrowIfNull(
            item.Decision);

        var earningsEvent =
            item.EarningsEvent;
        var decision =
            item.Decision;

        if (earningsEvent.PublishedAt ==
            default)
        {
            throw new ArgumentException(
                "EarningsEvent.PublishedAt is required.",
                nameof(item));
        }

        if (earningsEvent.Provenance is null)
        {
            throw new ArgumentException(
                "EarningsEvent.Provenance is required.",
                nameof(item));
        }

        if (earningsEvent.Provenance
                .SourceTimestamp >
            earningsEvent.PublishedAt)
        {
            throw new InvalidOperationException(
                "Look-ahead invariant violated: provenance SourceTimestamp must not be later than EarningsEvent.PublishedAt.");
        }

        if (earningsEvent.PublishedAt >
            earningsEvent.Provenance
                .RetrievedAt)
        {
            throw new InvalidOperationException(
                "Look-ahead invariant violated: EarningsEvent.PublishedAt must not be later than provenance RetrievedAt.");
        }

        if (decision.GeneratedAt <
            earningsEvent.PublishedAt)
        {
            throw new InvalidOperationException(
                "Look-ahead invariant violated: ResearchDecision.GeneratedAt must be at or after EarningsEvent.PublishedAt.");
        }

        if (!string.IsNullOrWhiteSpace(
                decision.SourceEventId) &&
            !string.Equals(
                decision.SourceEventId.Trim(),
                earningsEvent.EventId.Trim(),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "ResearchDecision.SourceEventId does not match the replayed EarningsEvent.");
        }
    }

    private static void ValidateSingleInstrumentSlice(
        IReadOnlyList<BacktestReplayItem> items,
        IReadOnlyList<MarketDataBar> bars,
        InstrumentReference instrument)
    {
        foreach (var item in items)
        {
            if (!BacktestInstrumentIdentity.IsSameInstrument(
                    item.Decision.Instrument,
                    instrument) ||
                !BacktestInstrumentIdentity.IsSameInstrument(
                    item.EarningsEvent.Instrument,
                    instrument))
            {
                throw new NotSupportedException(
                    "WS-04 bounded v1 replay supports one instrument per run.");
            }
        }

        foreach (var bar in bars)
        {
            if (!BacktestInstrumentIdentity.IsSameInstrument(
                    bar.Instrument,
                    instrument))
            {
                throw new NotSupportedException(
                    "WS-04 bounded v1 replay supports one instrument per run.");
            }
        }
    }

    private static void ValidateBar(
        MarketDataBar bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        ArgumentNullException.ThrowIfNull(
            bar.Instrument);

        if (bar.Instrument.AssetClass !=
            AssetClass.Stock)
        {
            throw new NotSupportedException(
                "WS-04 bounded v1 accepts daily stock bars only.");
        }

        if (bar.Period !=
            MarketDataBarPeriod.Daily)
        {
            throw new NotSupportedException(
                "WS-04 bounded v1 accepts MarketDataBarPeriod.Daily only.");
        }

        if (bar.OpenTime == default ||
            bar.CloseTime == default ||
            bar.CloseTime <=
            bar.OpenTime)
        {
            throw new ArgumentException(
                "Market-data bar requires a valid increasing open/close interval.",
                nameof(bar));
        }

        if (bar.OpenTime.UtcDateTime.Date !=
            bar.CloseTime.UtcDateTime.Date)
        {
            throw new NotSupportedException(
                "WS-04 bounded v1 daily bar open and close must belong to the same UTC trading date.");
        }

        if (bar.Open <= 0m ||
            bar.High <= 0m ||
            bar.Low <= 0m ||
            bar.Close <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bar),
                "OHLC prices must be greater than zero.");
        }

        if (bar.High <
                Math.Max(
                    bar.Open,
                    bar.Close) ||
            bar.Low >
                Math.Min(
                    bar.Open,
                    bar.Close) ||
            bar.High <
                bar.Low)
        {
            throw new ArgumentException(
                "Market-data bar OHLC relationship is invalid.",
                nameof(bar));
        }
    }

    private static void ValidateDailyBarSeries(
        IReadOnlyList<MarketDataBar> bars)
    {
        var duplicateTradingDate =
            bars
                .GroupBy(
                    bar =>
                        bar.OpenTime
                            .UtcDateTime
                            .Date)
                .FirstOrDefault(
                    group =>
                        group.Count() > 1);

        if (duplicateTradingDate is not null)
        {
            throw new NotSupportedException(
                $"WS-04 bounded v1 accepts at most one daily market bar per trading date. Duplicate date: {duplicateTradingDate.Key:yyyy-MM-dd}.");
        }
    }

    private static void ValidateExecutionCosts(
        BacktestExecutionCosts costs)
    {
        if (costs.CommissionPerOrder <
            0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(costs),
                "Commission per order cannot be negative.");
        }

        if (costs.SlippageBasisPoints <
                0m ||
            costs.SlippageBasisPoints >=
                10_000m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(costs),
                "Slippage basis points must be non-negative and less than 10,000.");
        }
    }

    private sealed record ScheduledReplay(
        BacktestReplayItem Item,
        MarketDataBar ExecutionBar);
}
