using TradeOps.Domain.Enums;

namespace TradeOps.Application.Models;

public sealed record FillAuditRecord(
    Guid FillId,
    Guid OrderId,
    string ExchangeFillId,
    string ClientOrderId,
    string Symbol,
    OrderSide Side,
    decimal Quantity,
    decimal Price,
    decimal? Fee,
    string? FeeCurrency,
    DateTimeOffset FilledAt);
