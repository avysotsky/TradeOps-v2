using System.Globalization;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Bybit;

internal static class BybitOrderValidator
{
    public static string? Validate(
        PlaceOrderRequest request,
        BybitInstrumentDto instrument)
    {
        if (!string.Equals(instrument.Status, "Trading", StringComparison.OrdinalIgnoreCase))
        {
            return $"Instrument {request.Symbol} is not in Trading status.";
        }

        var minQuantity = ParsePositiveDecimal(instrument.LotSizeFilter.MinOrderQty);
        var maxQuantity = ParsePositiveDecimal(
            request.OrderType == OrderType.Market
                ? instrument.LotSizeFilter.MaxMarketOrderQty
                : instrument.LotSizeFilter.MaxOrderQty);
        var quantityStep = ParsePositiveDecimal(instrument.LotSizeFilter.QuantityStep);

        if (minQuantity is not null && request.Quantity < minQuantity.Value)
        {
            return $"Quantity {request.Quantity} is below minimum {minQuantity.Value}.";
        }

        if (maxQuantity is not null && request.Quantity > maxQuantity.Value)
        {
            return $"Quantity {request.Quantity} exceeds maximum {maxQuantity.Value}.";
        }

        if (quantityStep is not null && !IsAligned(request.Quantity, quantityStep.Value))
        {
            return $"Quantity {request.Quantity} is not aligned to qtyStep {quantityStep.Value}.";
        }

        if (request.OrderType != OrderType.Limit)
        {
            return null;
        }

        if (request.Price is null)
        {
            return "Limit orders require Price.";
        }

        var price = request.Price.Value;
        var minPrice = ParsePositiveDecimal(instrument.PriceFilter.MinPrice);
        var maxPrice = ParsePositiveDecimal(instrument.PriceFilter.MaxPrice);
        var tickSize = ParsePositiveDecimal(instrument.PriceFilter.TickSize);

        if (minPrice is not null && price < minPrice.Value)
        {
            return $"Price {price} is below minimum {minPrice.Value}.";
        }

        if (maxPrice is not null && price > maxPrice.Value)
        {
            return $"Price {price} exceeds maximum {maxPrice.Value}.";
        }

        if (tickSize is not null && !IsAligned(price, tickSize.Value))
        {
            return $"Price {price} is not aligned to tickSize {tickSize.Value}.";
        }

        var minNotional = ParsePositiveDecimal(instrument.LotSizeFilter.MinNotionalValue);
        if (minNotional is not null && price * request.Quantity < minNotional.Value)
        {
            return $"Order notional {price * request.Quantity} is below minimum {minNotional.Value}.";
        }

        return null;
    }

    private static bool IsAligned(decimal value, decimal step)
    {
        if (step <= 0m)
        {
            return true;
        }

        return value % step == 0m;
    }

    private static decimal? ParsePositiveDecimal(string? value)
    {
        if (!decimal.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
            || parsed <= 0m)
        {
            return null;
        }

        return parsed;
    }
}
