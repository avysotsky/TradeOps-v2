using TradeOps.Domain.Enums;

namespace TradeOps.Application.Models;

public sealed record PlaceOrderRequest(
    string ClientOrderId,
    string Symbol,
    OrderSide Side,
    OrderType OrderType,
    decimal Quantity,
    decimal? Price = null);
