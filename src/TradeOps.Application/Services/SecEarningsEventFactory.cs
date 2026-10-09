using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Services;

public static class SecEarningsEventFactory
{
    public const string ProviderName = "SEC";
    public const string ExtractionMethodName = "sec-filing-xbrl-v1";

    public static EarningsEvent Create(
        SecFilingEarningsFacts facts,
        DateTimeOffset retrievedAt)
    {
        ArgumentNullException.ThrowIfNull(facts);

        ValidateRequired(
            facts.EventId,
            nameof(facts.EventId));
        ValidateRequired(
            facts.Symbol,
            nameof(facts.Symbol));
        ValidateRequired(
            facts.FiscalPeriod,
            nameof(facts.FiscalPeriod));
        ValidateRequired(
            facts.Cik,
            nameof(facts.Cik));
        ValidateRequired(
            facts.AccessionNumber,
            nameof(facts.AccessionNumber));
        ValidateRequired(
            facts.FormType,
            nameof(facts.FormType));
        ValidateRequired(
            facts.Currency,
            nameof(facts.Currency));

        if (facts.AcceptedAt == default)
        {
            throw new ArgumentException(
                "SEC AcceptedAt is required as the source acceptance timestamp.",
                nameof(facts));
        }

        if (!IsSecUri(
                facts.SourceUri))
        {
            throw new ArgumentException(
                "SourceUri must be an absolute SEC URL.",
                nameof(facts));
        }

        if (retrievedAt == default)
        {
            throw new ArgumentException(
                "RetrievedAt is required.",
                nameof(retrievedAt));
        }

        var acceptedAt =
            facts.AcceptedAt.ToUniversalTime();
        var normalizedRetrievedAt =
            retrievedAt.ToUniversalTime();

        if (normalizedRetrievedAt < acceptedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retrievedAt),
                "RetrievedAt cannot be earlier than the SEC acceptance timestamp.");
        }

        var publishedAt =
            ResolvePublishedAt(
                facts.PubliclyAvailableAt,
                acceptedAt,
                normalizedRetrievedAt);

        var snapshot =
            new EarningsSnapshot(
                Revenue: facts.Revenue,
                DilutedEps: facts.DilutedEps,
                NetIncome: facts.NetIncome,
                GrossMargin:
                    Divide(
                        facts.GrossProfit,
                        facts.Revenue),
                OperatingMargin:
                    Divide(
                        facts.OperatingIncome,
                        facts.Revenue),
                Guidance: facts.Guidance);

        var provenance =
            new ResearchSourceProvenance(
                ProviderName,
                facts.SourceUri,
                acceptedAt,
                normalizedRetrievedAt,
                ExtractionMethodName,
                SourceDocumentId:
                    facts.AccessionNumber.Trim(),
                IssuerId:
                    $"CIK:{facts.Cik.Trim()}");

        return new EarningsEvent(
            facts.EventId.Trim(),
            new InstrumentReference(
                facts.Symbol.Trim().ToUpperInvariant(),
                AssetClass.Stock,
                facts.Currency.Trim().ToUpperInvariant()),
            publishedAt,
            facts.FiscalPeriod.Trim(),
            snapshot,
            provenance);
    }

    private static DateTimeOffset ResolvePublishedAt(
        DateTimeOffset? publiclyAvailableAt,
        DateTimeOffset acceptedAt,
        DateTimeOffset retrievedAt)
    {
        if (!publiclyAvailableAt.HasValue)
        {
            return retrievedAt;
        }

        var availableAt =
            publiclyAvailableAt.Value.ToUniversalTime();

        if (availableAt < acceptedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(publiclyAvailableAt),
                "PubliclyAvailableAt cannot precede the SEC acceptance timestamp.");
        }

        if (availableAt > retrievedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(publiclyAvailableAt),
                "PubliclyAvailableAt cannot be later than RetrievedAt for the same acquisition observation.");
        }

        return availableAt;
    }

    private static bool IsSecUri(
        Uri sourceUri)
    {
        if (sourceUri is null
            || !sourceUri.IsAbsoluteUri)
        {
            return false;
        }

        var host =
            sourceUri.Host;

        return string.Equals(
                   host,
                   "sec.gov",
                   StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(
                   ".sec.gov",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static decimal? Divide(
        decimal? numerator,
        decimal? denominator)
    {
        if (!numerator.HasValue
            || !denominator.HasValue
            || denominator.Value == 0m)
        {
            return null;
        }

        return numerator.Value / denominator.Value;
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
