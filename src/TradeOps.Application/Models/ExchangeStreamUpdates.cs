using TradeOps.Domain.Enums;

namespace TradeOps.Application.Models;

public sealed record ExchangeOrderUpdate(
    string ExchangeOrderId,
    string ClientOrderId,
    string Symbol,
    OrderSide Side,
    OrderStatus Status,
    decimal RequestedQuantity,
    decimal FilledQuantity,
    decimal? AverageFillPrice,
    decimal? Price,
    DateTimeOffset OccurredAt);

public sealed record ExchangeExecutionUpdate(
    string ExchangeOrderId,
    string ClientOrderId,
    string ExecutionId,
    string Symbol,
    OrderSide Side,
    decimal Quantity,
    decimal Price,
    decimal? Fee,
    string? FeeCurrency,
    DateTimeOffset ExecutedAt);
