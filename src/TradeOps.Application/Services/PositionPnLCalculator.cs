using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Services;

public static class PositionPnLCalculator
{
    public static PositionState Calculate(
        string symbol,
        IEnumerable<PositionFill> fills,
        decimal? markPrice,
        DateTimeOffset calculatedAt)
    {
        var signedQuantity = 0m;
        var averageEntryPrice = 0m;
        var realizedPnL = 0m;

        foreach (var fill in fills
            .OrderBy(x => x.FilledAt)
            .ThenBy(x => x.ExchangeFillId, StringComparer.Ordinal))
        {
            if (fill.Quantity <= 0m || fill.Price <= 0m)
            {
                continue;
            }

            var fillSignedQuantity = fill.Side == OrderSide.Buy
                ? fill.Quantity
                : -fill.Quantity;

            if (signedQuantity == 0m)
            {
                signedQuantity = fillSignedQuantity;
                averageEntryPrice = fill.Price;
                continue;
            }

            var currentDirection = Math.Sign(signedQuantity);
            var fillDirection = Math.Sign(fillSignedQuantity);

            if (currentDirection == fillDirection)
            {
                var currentAbsoluteQuantity = Math.Abs(signedQuantity);
                var fillAbsoluteQuantity = Math.Abs(fillSignedQuantity);
                var newAbsoluteQuantity = currentAbsoluteQuantity + fillAbsoluteQuantity;

                averageEntryPrice =
                    ((averageEntryPrice * currentAbsoluteQuantity)
                    + (fill.Price * fillAbsoluteQuantity))
                    / newAbsoluteQuantity;

                signedQuantity += fillSignedQuantity;
                continue;
            }

            var closingQuantity = Math.Min(
                Math.Abs(signedQuantity),
                Math.Abs(fillSignedQuantity));

            realizedPnL += closingQuantity
                * (fill.Price - averageEntryPrice)
                * currentDirection;

            var newSignedQuantity = signedQuantity + fillSignedQuantity;

            if (newSignedQuantity == 0m)
            {
                signedQuantity = 0m;
                averageEntryPrice = 0m;
                continue;
            }

            if (Math.Sign(newSignedQuantity) != currentDirection)
            {
                averageEntryPrice = fill.Price;
            }

            signedQuantity = newSignedQuantity;
        }

        var quantity = Math.Abs(signedQuantity);
        var side = signedQuantity switch
        {
            > 0m => OrderSide.Buy,
            < 0m => OrderSide.Sell,
            _ => (OrderSide?)null
        };

        decimal? unrealizedPnL;
        decimal? totalPnL;

        if (signedQuantity == 0m)
        {
            unrealizedPnL = 0m;
            totalPnL = realizedPnL;
        }
        else if (markPrice is > 0m)
        {
            unrealizedPnL = quantity
                * (markPrice.Value - averageEntryPrice)
                * Math.Sign(signedQuantity);
            totalPnL = realizedPnL + unrealizedPnL.Value;
        }
        else
        {
            unrealizedPnL = null;
            totalPnL = null;
        }

        return new PositionState(
            symbol,
            side,
            quantity,
            averageEntryPrice,
            realizedPnL,
            markPrice,
            unrealizedPnL,
            totalPnL,
            calculatedAt);
    }
}
