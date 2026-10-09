using TradeOps.Application.Models;

namespace TradeOps.Application.Services.Backtesting;

internal static class BacktestInstrumentIdentity
{
    public static bool IsSameInstrument(
        InstrumentReference current,
        InstrumentReference target)
    {
        if (current.AssetClass !=
            target.AssetClass)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(
                current.VenueInstrumentId) &&
            !string.IsNullOrWhiteSpace(
                target.VenueInstrumentId))
        {
            return string.Equals(
                current.VenueInstrumentId.Trim(),
                target.VenueInstrumentId.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        if (!string.Equals(
                current.Symbol.Trim(),
                target.Symbol,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(
                   current.Currency) ||
               string.IsNullOrWhiteSpace(
                   target.Currency) ||
               string.Equals(
                   current.Currency.Trim(),
                   target.Currency,
                   StringComparison.OrdinalIgnoreCase);
    }
}
