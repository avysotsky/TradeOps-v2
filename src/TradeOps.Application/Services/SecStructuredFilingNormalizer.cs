using TradeOps.Application.Models;

namespace TradeOps.Application.Services;

public static class SecStructuredFilingNormalizer
{
    private const string UsGaapNamespace =
        "us-gaap";

    private static readonly string[] RevenueConcepts =
    [
        "RevenueFromContractWithCustomerExcludingAssessedTax",
        "SalesRevenueNet",
        "Revenues"
    ];

    private static readonly string[] DilutedEpsConcepts =
    [
        "EarningsPerShareDiluted"
    ];

    private static readonly string[] NetIncomeConcepts =
    [
        "NetIncomeLoss",
        "ProfitLoss"
    ];

    private static readonly string[] GrossProfitConcepts =
    [
        "GrossProfit"
    ];

    private static readonly string[] OperatingIncomeConcepts =
    [
        "OperatingIncomeLoss"
    ];

    public static SecFilingEarningsFacts Normalize(
        SecStructuredFiling filing)
    {
        ArgumentNullException.ThrowIfNull(filing);

        ValidateRequired(
            filing.EventId,
            nameof(filing.EventId));
        ValidateRequired(
            filing.Symbol,
            nameof(filing.Symbol));
        ValidateRequired(
            filing.FiscalPeriod,
            nameof(filing.FiscalPeriod));
        ValidateRequired(
            filing.Cik,
            nameof(filing.Cik));
        ValidateRequired(
            filing.AccessionNumber,
            nameof(filing.AccessionNumber));
        ValidateRequired(
            filing.FormType,
            nameof(filing.FormType));
        ValidateRequired(
            filing.Currency,
            nameof(filing.Currency));

        if (filing.AcceptedAt == default)
        {
            throw new ArgumentException(
                "SEC AcceptedAt is required.",
                nameof(filing));
        }

        if (filing.FiscalPeriodStart == default
            || filing.FiscalPeriodEnd == default
            || filing.FiscalPeriodStart >
               filing.FiscalPeriodEnd)
        {
            throw new ArgumentException(
                "A valid SEC fiscal-period interval is required.",
                nameof(filing));
        }

        if (filing.Facts is null)
        {
            throw new ArgumentException(
                "SEC structured facts are required.",
                nameof(filing));
        }

        var currency =
            filing.Currency
                .Trim()
                .ToUpperInvariant();

        return new SecFilingEarningsFacts(
            filing.EventId.Trim(),
            filing.Symbol.Trim().ToUpperInvariant(),
            filing.FiscalPeriod.Trim(),
            filing.Cik.Trim(),
            filing.AccessionNumber.Trim(),
            filing.FormType.Trim().ToUpperInvariant(),
            filing.SourceUri,
            filing.AcceptedAt,
            currency,
            Revenue:
                SelectFact(
                    filing,
                    RevenueConcepts,
                    currency),
            DilutedEps:
                SelectFact(
                    filing,
                    DilutedEpsConcepts,
                    $"{currency}/shares"),
            NetIncome:
                SelectFact(
                    filing,
                    NetIncomeConcepts,
                    currency),
            GrossProfit:
                SelectFact(
                    filing,
                    GrossProfitConcepts,
                    currency),
            OperatingIncome:
                SelectFact(
                    filing,
                    OperatingIncomeConcepts,
                    currency),
            Guidance:
                filing.Guidance,
            PubliclyAvailableAt:
                filing.PubliclyAvailableAt);
    }

    private static decimal? SelectFact(
        SecStructuredFiling filing,
        IEnumerable<string> preferredConcepts,
        string requiredUnit)
    {
        foreach (var concept in preferredConcepts)
        {
            var candidates =
                filing.Facts
                    .Where(
                        fact =>
                            IsExactFilingFact(
                                filing,
                                fact,
                                concept,
                                requiredUnit))
                    .ToArray();

            if (candidates.Length > 1)
            {
                throw new InvalidOperationException(
                    $"SEC structured facts are ambiguous for {concept}, accession {filing.AccessionNumber}, period {filing.FiscalPeriodStart:yyyy-MM-dd}..{filing.FiscalPeriodEnd:yyyy-MM-dd}.");
            }

            if (candidates.Length == 1)
            {
                return candidates[0].Value;
            }
        }

        return null;
    }

    private static bool IsExactFilingFact(
        SecStructuredFiling filing,
        SecStructuredFact fact,
        string concept,
        string requiredUnit)
    {
        if (fact is null)
        {
            return false;
        }

        return
            string.Equals(
                fact.Namespace?.Trim(),
                UsGaapNamespace,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                fact.Concept?.Trim(),
                concept,
                StringComparison.Ordinal)
            && string.Equals(
                fact.Unit?.Trim(),
                requiredUnit,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                fact.AccessionNumber?.Trim(),
                filing.AccessionNumber.Trim(),
                StringComparison.Ordinal)
            && string.Equals(
                fact.FormType?.Trim(),
                filing.FormType.Trim(),
                StringComparison.OrdinalIgnoreCase)
            && fact.PeriodStart ==
               filing.FiscalPeriodStart
            && fact.PeriodEnd ==
               filing.FiscalPeriodEnd
            && string.IsNullOrWhiteSpace(
                fact.DimensionKey);
    }

    private static void ValidateRequired(
        string? value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"{parameterName} is required.",
                parameterName);
        }
    }
}
