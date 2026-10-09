using Xunit;
using TradeOps.Application.Models;
using TradeOps.Application.Services.Backtesting;
using TradeOps.Domain.Enums;

namespace TradeOps.UnitTests;

public sealed class EventDrivenBacktesterTests
{
    private static readonly InstrumentReference Instrument =
        new(
            "AAPL",
            AssetClass.Stock,
            "USD",
            Exchange: "NASDAQ");

    [Fact]
    public void DeterministicClock_orders_and_deduplicates_instants()
    {
        var earlier =
            new DateTimeOffset(
                2026,
                1,
                2,
                14,
                30,
                0,
                TimeSpan.Zero);
        var later =
            earlier.AddHours(6);

        var clock =
            new DeterministicEventClock(
                new[]
                {
                    later,
                    earlier,
                    earlier.ToOffset(
                        TimeSpan.FromHours(2))
                });

        Assert.Equal(
            2,
            clock.Count);

        Assert.True(
            clock.MoveNext());
        Assert.Equal(
            earlier,
            clock.Current);

        Assert.True(
            clock.MoveNext());
        Assert.Equal(
            later,
            clock.Current);

        Assert.False(
            clock.MoveNext());
    }

    [Fact]
    public void Replay_rejects_decision_generated_before_published_at()
    {
        var publishedAt =
            Utc(
                2026,
                1,
                2,
                21,
                30);

        var item =
            CreateReplayItem(
                publishedAt,
                publishedAt.AddMinutes(-1));

        var bars =
            new[]
            {
                Bar(
                    2026,
                    1,
                    3,
                    100m,
                    101m)
            };

        var sut =
            new EventDrivenBacktester();

        var exception =
            Assert.Throws<InvalidOperationException>(
                () =>
                    sut.Run(
                        new[] { item },
                        bars,
                        10_000m));

        Assert.Contains(
            "GeneratedAt",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Replay_after_hours_event_uses_next_bar_open_not_earlier_close()
    {
        var publishedAt =
            Utc(
                2026,
                1,
                2,
                21,
                30);

        var item =
            CreateReplayItem(
                publishedAt,
                publishedAt.AddMinutes(1));

        var bars =
            new[]
            {
                Bar(
                    2026,
                    1,
                    2,
                    100m,
                    101m),
                Bar(
                    2026,
                    1,
                    3,
                    110m,
                    112m)
            };

        var result =
            new EventDrivenBacktester()
                .Run(
                    new[] { item },
                    bars,
                    10_000m);

        var fill =
            Assert.Single(
                result.Fills);

        Assert.Equal(
            bars[1].OpenTime,
            fill.ExecutedAt);
        Assert.Equal(
            110m,
            fill.ReferencePrice);
        Assert.NotEqual(
            bars[0].Close,
            fill.ReferencePrice);

        var plan =
            Assert.Single(
                result.Plans);

        Assert.Equal(
            bars[1].OpenTime,
            plan.PlannedAt);
        Assert.Equal(
            RebalancePlanStatus.Ready,
            plan.Status);
        Assert.NotNull(
            plan.OrderIntent);
    }

    [Fact]
    public void Replay_waits_until_decision_generated_at_before_snapshot_and_rebalance()
    {
        var publishedAt =
            Utc(
                2026,
                1,
                2,
                13,
                0);
        var generatedAt =
            Utc(
                2026,
                1,
                2,
                22,
                0);

        var item =
            CreateReplayItem(
                publishedAt,
                generatedAt);

        var bars =
            new[]
            {
                Bar(
                    2026,
                    1,
                    2,
                    100m,
                    101m),
                Bar(
                    2026,
                    1,
                    3,
                    103m,
                    104m)
            };

        var result =
            new EventDrivenBacktester()
                .Run(
                    new[] { item },
                    bars,
                    10_000m);

        var plan =
            Assert.Single(
                result.Plans);

        Assert.True(
            plan.PlannedAt >=
            generatedAt);
        Assert.Equal(
            bars[1].OpenTime,
            plan.PlannedAt);
        Assert.DoesNotContain(
            plan.ConstraintViolations,
            violation =>
                violation.Code ==
                "DecisionNotYetAvailable");
    }

    [Fact]
    public void Replay_applies_configured_commission_and_slippage_deterministically()
    {
        var publishedAt =
            Utc(
                2026,
                1,
                2,
                13,
                0);

        var item =
            CreateReplayItem(
                publishedAt,
                publishedAt.AddMinutes(1));

        var bars =
            new[]
            {
                Bar(
                    2026,
                    1,
                    2,
                    100m,
                    100m),
                Bar(
                    2026,
                    1,
                    3,
                    100m,
                    102m)
            };

        var costs =
            new BacktestExecutionCosts(
                CommissionPerOrder: 2m,
                SlippageBasisPoints: 25m);

        var sut =
            new EventDrivenBacktester();

        var first =
            sut.Run(
                new[] { item },
                bars,
                10_000m,
                executionCosts: costs);

        var second =
            sut.Run(
                new[] { item },
                bars,
                10_000m,
                executionCosts: costs);

        var fill =
            Assert.Single(
                first.Fills);

        Assert.Equal(
            50m,
            fill.Quantity);
        Assert.Equal(
            100.25m,
            fill.ExecutionPrice);
        Assert.Equal(
            12.50m,
            fill.SlippageCost);
        Assert.Equal(
            2m,
            fill.Commission);
        Assert.Equal(
            4_985.50m,
            first.FinalPortfolio.Cash);
        Assert.Equal(
            50m,
            Assert.Single(
                    first.FinalPortfolio
                        .Positions)
                .Quantity);

        Assert.Equal(
            first.Fills.ToArray(),
            second.Fills.ToArray());
        Assert.Equal(
            first.EquityCurve.ToArray(),
            second.EquityCurve.ToArray());
        Assert.Equal(
            first.Metrics,
            second.Metrics);
        Assert.Equal(
            first.FinalPortfolio.Cash,
            second.FinalPortfolio.Cash);
        Assert.Equal(
            first.FinalPortfolio.NetAssetValue,
            second.FinalPortfolio.NetAssetValue);
        Assert.Equal(
            1,
            first.Metrics.EventCount);
        Assert.True(
            first.Metrics.Turnover >
            0m);
    }

    [Fact]
    public void Replay_same_symbol_currency_but_different_venue_ids_are_not_same_instrument()
    {
        var publishedAt =
            Utc(
                2026,
                1,
                2,
                13,
                0);

        var decisionInstrument =
            new InstrumentReference(
                "AAPL",
                AssetClass.Stock,
                "USD",
                VenueInstrumentId: "111",
                Exchange: "NASDAQ");

        var barInstrument =
            decisionInstrument with
            {
                VenueInstrumentId = "222"
            };

        var item =
            CreateReplayItem(
                publishedAt,
                publishedAt.AddMinutes(1),
                decisionInstrument);

        var exception =
            Assert.Throws<NotSupportedException>(
                () =>
                    new EventDrivenBacktester()
                        .Run(
                            new[] { item },
                            new[]
                            {
                                Bar(
                                    2026,
                                    1,
                                    2,
                                    100m,
                                    101m,
                                    barInstrument)
                            },
                            10_000m));

        Assert.Contains(
            "one instrument",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Replay_same_venue_id_is_same_instrument_even_when_symbol_differs()
    {
        var publishedAt =
            Utc(
                2026,
                1,
                2,
                13,
                0);

        var decisionInstrument =
            new InstrumentReference(
                "AAPL",
                AssetClass.Stock,
                "USD",
                VenueInstrumentId: "111",
                Exchange: "NASDAQ");

        var barInstrument =
            new InstrumentReference(
                "AAPL_ALIAS",
                AssetClass.Stock,
                "USD",
                VenueInstrumentId: "111",
                Exchange: "NASDAQ");

        var result =
            new EventDrivenBacktester()
                .Run(
                    new[]
                    {
                        CreateReplayItem(
                            publishedAt,
                            publishedAt.AddMinutes(1),
                            decisionInstrument)
                    },
                    new[]
                    {
                        Bar(
                            2026,
                            1,
                            2,
                            100m,
                            101m,
                            barInstrument)
                    },
                    10_000m);

        Assert.Single(
            result.Fills);
    }

    [Fact]
    public void Replay_without_venue_id_falls_back_to_symbol_and_currency()
    {
        var publishedAt =
            Utc(
                2026,
                1,
                2,
                13,
                0);

        var decisionInstrument =
            new InstrumentReference(
                "AAPL",
                AssetClass.Stock,
                "USD");

        var barInstrument =
            new InstrumentReference(
                "aapl",
                AssetClass.Stock,
                "USD");

        var result =
            new EventDrivenBacktester()
                .Run(
                    new[]
                    {
                        CreateReplayItem(
                            publishedAt,
                            publishedAt.AddMinutes(1),
                            decisionInstrument)
                    },
                    new[]
                    {
                        Bar(
                            2026,
                            1,
                            2,
                            100m,
                            101m,
                            barInstrument)
                    },
                    10_000m);

        Assert.Single(
            result.Fills);
    }

    [Fact]
    public void Replay_rejects_non_daily_market_bar_period()
    {
        var publishedAt =
            Utc(
                2026,
                1,
                2,
                13,
                0);

        var exception =
            Assert.Throws<NotSupportedException>(
                () =>
                    new EventDrivenBacktester()
                        .Run(
                            new[]
                            {
                                CreateReplayItem(
                                    publishedAt,
                                    publishedAt.AddMinutes(1))
                            },
                            new[]
                            {
                                Bar(
                                    2026,
                                    1,
                                    2,
                                    100m,
                                    101m,
                                    period:
                                        MarketDataBarPeriod
                                            .Intraday)
                            },
                            10_000m));

        Assert.Contains(
            "Daily",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Replay_rejects_duplicate_daily_bars_for_same_trading_date()
    {
        var publishedAt =
            Utc(
                2026,
                1,
                1,
                13,
                0);

        var exception =
            Assert.Throws<NotSupportedException>(
                () =>
                    new EventDrivenBacktester()
                        .Run(
                            new[]
                            {
                                CreateReplayItem(
                                    publishedAt,
                                    publishedAt.AddMinutes(1))
                            },
                            new[]
                            {
                                Bar(
                                    2026,
                                    1,
                                    2,
                                    100m,
                                    101m),
                                Bar(
                                    2026,
                                    1,
                                    2,
                                    102m,
                                    103m)
                            },
                            10_000m));

        Assert.Contains(
            "at most one daily market bar",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Replay_enforces_published_at_provenance_interval(
        bool sourceAfterPublished)
    {
        var publishedAt =
            Utc(
                2026,
                1,
                2,
                21,
                30);

        var sourceTimestamp =
            sourceAfterPublished
                ? publishedAt.AddMinutes(1)
                : publishedAt.AddMinutes(-2);
        var retrievedAt =
            sourceAfterPublished
                ? publishedAt.AddMinutes(2)
                : publishedAt.AddMinutes(-1);

        var earningsEvent =
            CreateEarningsEvent(
                publishedAt,
                sourceTimestamp,
                retrievedAt);

        var decision =
            CreateDecision(
                publishedAt.AddMinutes(3),
                earningsEvent.EventId);

        var item =
            new BacktestReplayItem(
                earningsEvent,
                decision);

        var exception =
            Assert.Throws<InvalidOperationException>(
                () =>
                    new EventDrivenBacktester()
                        .Run(
                            new[] { item },
                            new[]
                            {
                                Bar(
                                    2026,
                                    1,
                                    3,
                                    100m,
                                    101m)
                            },
                            10_000m));

        Assert.Contains(
            "Look-ahead invariant",
            exception.Message,
            StringComparison.Ordinal);
    }

    private static BacktestReplayItem
        CreateReplayItem(
            DateTimeOffset publishedAt,
            DateTimeOffset generatedAt,
            InstrumentReference? instrument = null)
    {
        var replayInstrument =
            instrument ??
            Instrument;

        var earningsEvent =
            CreateEarningsEvent(
                publishedAt,
                publishedAt.AddMinutes(-1),
                publishedAt.AddMinutes(2),
                replayInstrument);

        return new BacktestReplayItem(
            earningsEvent,
            CreateDecision(
                generatedAt,
                earningsEvent.EventId,
                replayInstrument));
    }

    private static EarningsEvent
        CreateEarningsEvent(
            DateTimeOffset publishedAt,
            DateTimeOffset sourceTimestamp,
            DateTimeOffset retrievedAt,
            InstrumentReference? instrument = null) =>
        new(
            "earnings-aapl-2025q4",
            instrument ??
            Instrument,
            publishedAt,
            "2025-Q4",
            new EarningsSnapshot(
                Revenue: 100m,
                DilutedEps: 2m),
            new ResearchSourceProvenance(
                "SEC",
                new Uri(
                    "https://www.sec.gov/Archives/example"),
                sourceTimestamp,
                retrievedAt,
                "fixture",
                "0000000000-26-000001",
                "CIK:0000320193"));

    private static ResearchDecision
        CreateDecision(
            DateTimeOffset generatedAt,
            string eventId,
            InstrumentReference? instrument = null) =>
        new(
            "decision-aapl-2025q4",
            "earnings-policy-v1",
            instrument ??
            Instrument,
            ResearchDecisionAction
                .SetTargetWeight,
            generatedAt,
            TargetWeight: 0.50m,
            SourceEventId: eventId,
            Reason: "Deterministic fixture.");

    private static MarketDataBar Bar(
        int year,
        int month,
        int day,
        decimal open,
        decimal close,
        InstrumentReference? instrument = null,
        MarketDataBarPeriod period =
            MarketDataBarPeriod.Daily)
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
            instrument ??
            Instrument,
            period,
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
