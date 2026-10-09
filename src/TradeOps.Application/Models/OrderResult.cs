using TradeOps.Domain.Enums;

namespace TradeOps.Application.Models;

public sealed record OrderResult(
    string? ExchangeOrderId,
    string ClientOrderId,
    OrderStatus Status,
    decimal FilledQuantity,
    decimal? AverageFillPrice);
