using TradeOps.Application.Models;

namespace TradeOps.Application.Services;

public static class PnLAccountingCalculator
{
    public static PnLAccountingSnapshot CalculateDaily(
        IReadOnlyCollection<PositionFill> fills,
        string settlementCurrency,
        DateTimeOffset now)
    {
        var normalizedSettlementCurrency = NormalizeSettlementCurrency(settlementCurrency);
        var utcNow = now.ToUniversalTime();
        var dayStart = new DateTimeOffset(utcNow.UtcDateTime.Date, TimeSpan.Zero);

        var currentGross = CalculateGrossRealized(
            fills.Where(fill => fill.FilledAt <= utcNow),
            utcNow);
        var grossBeforeDay = CalculateGrossRealized(
            fills.Where(fill => fill.FilledAt < dayStart),
            dayStart);
        var dailyGross = currentGross - grossBeforeDay;

        var settlementFees = 0m;
        var unconvertedFees = new List<UnconvertedFee>();

        foreach (var fill in fills
            .Where(fill => fill.FilledAt >= dayStart && fill.FilledAt <= utcNow)
            .OrderBy(fill => fill.FilledAt)
            .ThenBy(fill => fill.ExchangeFillId, StringComparer.Ordinal))
        {
            if (fill.Fee is null || fill.Fee.Value == 0m)
            {
                continue;
            }

            if (string.Equals(
                fill.FeeCurrency?.Trim(),
                normalizedSettlementCurrency,
                StringComparison.OrdinalIgnoreCase))
            {
                settlementFees += fill.Fee.Value;
                continue;
            }

            unconvertedFees.Add(new UnconvertedFee(
                fill.ExchangeFillId,
                fill.Fee.Value,
                string.IsNullOrWhiteSpace(fill.FeeCurrency)
                    ? null
                    : fill.FeeCurrency.Trim().ToUpperInvariant(),
                fill.FilledAt));
        }

        decimal? netRealized = unconvertedFees.Count == 0
            ? dailyGross - settlementFees
            : null;

        return new PnLAccountingSnapshot(
            normalizedSettlementCurrency,
            dailyGross,
            settlementFees,
            netRealized,
            unconvertedFees,
            utcNow);
    }

    private static decimal CalculateGrossRealized(
        IEnumerable<PositionFill> fills,
        DateTimeOffset calculatedAt)
    {
        return fills
            .GroupBy(fill => fill.Symbol, StringComparer.OrdinalIgnoreCase)
            .Sum(group => PositionPnLCalculator.Calculate(
                group.Key,
                group,
                null,
                calculatedAt).RealizedPnL);
    }

    private static string NormalizeSettlementCurrency(string settlementCurrency)
    {
        if (string.IsNullOrWhiteSpace(settlementCurrency))
        {
            throw new ArgumentException(
                "Settlement currency is required for PnL accounting.",
                nameof(settlementCurrency));
        }

        return settlementCurrency.Trim().ToUpperInvariant();
    }
}
