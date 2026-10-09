using TradeOps.Domain.Enums;

namespace TradeOps.Application.Models;

public sealed record PositionFill(
    string Symbol,
    OrderSide Side,
    decimal Quantity,
    decimal Price,
    DateTimeOffset FilledAt,
    string ExchangeFillId,
    decimal? Fee = null,
    string? FeeCurrency = null);

public sealed record PositionState(
    string Symbol,
    OrderSide? Side,
    decimal Quantity,
    decimal AverageEntryPrice,
    decimal RealizedPnL,
    decimal? MarkPrice,
    decimal? UnrealizedPnL,
    decimal? TotalPnL,
    DateTimeOffset CalculatedAt);
