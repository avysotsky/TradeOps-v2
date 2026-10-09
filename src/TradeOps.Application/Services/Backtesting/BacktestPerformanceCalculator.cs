using TradeOps.Application.Models;

namespace TradeOps.Application.Services.Backtesting;

public static class BacktestPerformanceCalculator
{
    private const double DailyTradingObservationsPerYear = 252d;

    public static BacktestPerformanceMetrics Calculate(
        IReadOnlyList<BacktestEquityPoint> equityCurve,
        int eventCount,
        decimal totalTurnoverNotional,
        decimal initialCapital)
    {
        ArgumentNullException.ThrowIfNull(equityCurve);

        if (equityCurve.Count == 0)
        {
            throw new ArgumentException(
                "Equity curve must contain at least one point.",
                nameof(equityCurve));
        }

        if (initialCapital <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(initialCapital),
                "Initial capital must be greater than zero.");
        }

        var first =
            equityCurve[0];
        var last =
            equityCurve[^1];

        if (first.Equity <= 0m ||
            last.Equity <= 0m)
        {
            throw new InvalidOperationException(
                "Equity must stay greater than zero for bounded v1 metrics.");
        }

        var totalReturn =
            last.Equity /
            first.Equity -
            1m;

        var elapsedDays =
            (last.AsOf - first.AsOf)
            .TotalDays;

        decimal? cagr =
            elapsedDays > 0d
                ? (decimal)(
                    Math.Pow(
                        (double)(last.Equity / first.Equity),
                        365.25d / elapsedDays) -
                    1d)
                : null;

        var maxDrawdown =
            CalculateMaxDrawdown(
                equityCurve);

        var returns =
            CalculateReturns(
                equityCurve);

        var sharpe =
            CalculateSharpe(
                returns);

        var sortino =
            CalculateSortino(
                returns);

        var averageExposure =
            equityCurve
                .Where(point => point.Equity > 0m)
                .Select(
                    point =>
                        point.GrossExposure /
                        point.Equity)
                .DefaultIfEmpty(0m)
                .Average();

        return new BacktestPerformanceMetrics(
            eventCount,
            totalReturn,
            cagr,
            maxDrawdown,
            sharpe,
            sortino,
            totalTurnoverNotional /
            initialCapital,
            averageExposure);
    }

    private static decimal CalculateMaxDrawdown(
        IReadOnlyList<BacktestEquityPoint> equityCurve)
    {
        var peak =
            equityCurve[0].Equity;
        var maxDrawdown =
            0m;

        foreach (var point in equityCurve)
        {
            if (point.Equity > peak)
            {
                peak = point.Equity;
            }

            var drawdown =
                point.Equity /
                peak -
                1m;

            if (drawdown < maxDrawdown)
            {
                maxDrawdown = drawdown;
            }
        }

        return maxDrawdown;
    }

    private static IReadOnlyList<double> CalculateReturns(
        IReadOnlyList<BacktestEquityPoint> equityCurve)
    {
        var returns =
            new List<double>(
                Math.Max(
                    0,
                    equityCurve.Count - 1));

        for (var index = 1;
             index < equityCurve.Count;
             index++)
        {
            var previous =
                equityCurve[index - 1].Equity;
            var current =
                equityCurve[index].Equity;

            if (previous <= 0m)
            {
                throw new InvalidOperationException(
                    "Equity must stay greater than zero to calculate returns.");
            }

            returns.Add(
                (double)(
                    current /
                    previous -
                    1m));
        }

        return returns;
    }

    private static decimal? CalculateSharpe(
        IReadOnlyList<double> returns)
    {
        if (returns.Count < 2)
        {
            return null;
        }

        var mean =
            returns.Average();
        var variance =
            returns.Sum(
                value =>
                    Math.Pow(
                        value - mean,
                        2d)) /
            (returns.Count - 1);

        if (variance <= 0d)
        {
            return null;
        }

        return (decimal)(
            mean /
            Math.Sqrt(variance) *
            Math.Sqrt(DailyTradingObservationsPerYear));
    }

    private static decimal? CalculateSortino(
        IReadOnlyList<double> returns)
    {
        if (returns.Count == 0)
        {
            return null;
        }

        var downside =
            returns
                .Where(value => value < 0d)
                .ToArray();

        if (downside.Length == 0)
        {
            return null;
        }

        var downsideDeviation =
            Math.Sqrt(
                downside.Average(
                    value =>
                        value * value));

        if (downsideDeviation <= 0d)
        {
            return null;
        }

        return (decimal)(
            returns.Average() /
            downsideDeviation *
            Math.Sqrt(DailyTradingObservationsPerYear));
    }
}
