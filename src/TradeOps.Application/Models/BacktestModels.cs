using TradeOps.Domain.Enums;

namespace TradeOps.Application.Models;

public enum MarketDataBarPeriod
{
    Unknown = 0,
    Daily = 1,
    Intraday = 2,
    Weekly = 3
}

public sealed record MarketDataBar(
    InstrumentReference Instrument,
    MarketDataBarPeriod Period,
    DateTimeOffset OpenTime,
    DateTimeOffset CloseTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close);

public sealed record BacktestReplayItem(
    EarningsEvent EarningsEvent,
    ResearchDecision Decision);

public sealed record BacktestExecutionCosts(
    decimal CommissionPerOrder = 0m,
    decimal SlippageBasisPoints = 0m);

public sealed record BacktestFill(
    string DecisionId,
    InstrumentReference Instrument,
    OrderSide Side,
    DateTimeOffset ExecutedAt,
    decimal Quantity,
    decimal ReferencePrice,
    decimal ExecutionPrice,
    decimal GrossNotional,
    decimal Commission,
    decimal SlippageCost);

public sealed record BacktestEquityPoint(
    DateTimeOffset AsOf,
    decimal Equity,
    decimal GrossExposure);

public sealed record BacktestPerformanceMetrics(
    int EventCount,
    decimal TotalReturn,
    decimal? Cagr,
    decimal MaxDrawdown,
    decimal? Sharpe,
    decimal? Sortino,
    decimal Turnover,
    decimal AverageExposure);

public sealed record BacktestRunResult(
    IReadOnlyList<RebalancePlan> Plans,
    IReadOnlyList<BacktestFill> Fills,
    IReadOnlyList<BacktestEquityPoint> EquityCurve,
    PortfolioSnapshot FinalPortfolio,
    BacktestPerformanceMetrics Metrics);
