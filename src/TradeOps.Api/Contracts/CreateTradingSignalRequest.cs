using TradeOps.Domain.Enums;

namespace TradeOps.Api.Contracts;

public sealed record CreateTradingSignalRequest(
    string Symbol,
    OrderSide Side,
    decimal Quantity,
    decimal? RiskPercent = null,
    decimal? StopLoss = null,
    decimal? TakeProfit = null,
    string? Source = null,
    Guid? SignalId = null);
