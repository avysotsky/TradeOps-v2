using TradeOps.Application.Models;

namespace TradeOps.Application.Services;

public static class EarningsTranscriptFactSetMapper
{
    public static EarningsSnapshot ToSnapshot(
        EarningsTranscriptFactSet factSet)
    {
        ArgumentNullException.ThrowIfNull(factSet);

        EarningsGuidanceSnapshot? guidance =
            null;

        if (factSet.Guidance is not null)
        {
            guidance =
                new EarningsGuidanceSnapshot(
                    factSet.Guidance.Direction?.Direction
                    ?? EarningsGuidanceDirection.Unknown,
                    factSet.Guidance.RevenueLow?.Value,
                    factSet.Guidance.RevenueHigh?.Value,
                    factSet.Guidance.DilutedEpsLow?.Value,
                    factSet.Guidance.DilutedEpsHigh?.Value);
        }

        return new EarningsSnapshot(
            Revenue:
                factSet.Revenue?.Value,
            DilutedEps:
                factSet.DilutedEps?.Value,
            NetIncome:
                factSet.NetIncome?.Value,
            GrossMargin:
                factSet.GrossMargin?.Value,
            OperatingMargin:
                factSet.OperatingMargin?.Value,
            Guidance:
                guidance);
    }
}

public static class EarningsTranscriptEventFactory
{
    public const string ExtractionMethodName =
        "docflow-structured-earnings-v1";

    public static EarningsEvent Create(
        EarningsTranscriptResearchInput input,
        EarningsTranscriptFactSet factSet)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Instrument);
        ArgumentNullException.ThrowIfNull(input.Provenance);
        ArgumentNullException.ThrowIfNull(factSet);

        ValidateIdentity(
            input,
            factSet);

        ValidateAvailability(
            input);

        var provenance =
            new ResearchSourceProvenance(
                input.Provenance.Provider,
                input.Provenance.SourceUri,
                input.Provenance.SourceTimestamp,
                input.Provenance.RetrievedAt,
                ExtractionMethodName,
                input.Provenance.SourceDocumentId,
                input.Provenance.IssuerId);

        return new EarningsEvent(
            input.EarningsEventAssociationId,
            input.Instrument,
            input.PublishedAt,
            input.FiscalPeriod,
            EarningsTranscriptFactSetMapper.ToSnapshot(
                factSet),
            provenance);
    }

    private static void ValidateIdentity(
        EarningsTranscriptResearchInput input,
        EarningsTranscriptFactSet factSet)
    {
        if (factSet.SchemaVersion !=
            DocFlowStructuredEarningsFactsAdapter.SupportedSchemaVersion)
        {
            throw new ArgumentException(
                "Fact set schema version is not supported.",
                nameof(factSet));
        }

        if (!string.Equals(
                factSet.DocFlowDocumentId,
                input.DocFlowDocumentId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Fact set DocFlow document ID does not match the transcript research input.",
                nameof(factSet));
        }

        if (!string.Equals(
                factSet.DocFlowFingerprint,
                input.DocFlowFingerprint,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Fact set DocFlow fingerprint does not match the transcript research input.",
                nameof(factSet));
        }

        if (!string.Equals(
                factSet.Currency,
                input.Instrument.Currency,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Fact set currency does not match the transcript research input instrument.",
                nameof(factSet));
        }
    }

    private static void ValidateAvailability(
        EarningsTranscriptResearchInput input)
    {
        if (string.IsNullOrWhiteSpace(
                input.EarningsEventAssociationId))
        {
            throw new ArgumentException(
                "EarningsEventAssociationId is required.",
                nameof(input));
        }

        if (string.IsNullOrWhiteSpace(
                input.FiscalPeriod))
        {
            throw new ArgumentException(
                "FiscalPeriod is required.",
                nameof(input));
        }

        if (input.PublishedAt == default)
        {
            throw new ArgumentException(
                "PublishedAt is required.",
                nameof(input));
        }

        if (input.Provenance.SourceTimestamp == default
            || input.Provenance.RetrievedAt == default)
        {
            throw new ArgumentException(
                "SourceTimestamp and RetrievedAt are required.",
                nameof(input));
        }

        var sourceTimestamp =
            input.Provenance.SourceTimestamp
                .ToUniversalTime();

        var publishedAt =
            input.PublishedAt
                .ToUniversalTime();

        var retrievedAt =
            input.Provenance.RetrievedAt
                .ToUniversalTime();

        if (sourceTimestamp > publishedAt
            || publishedAt > retrievedAt)
        {
            throw new ArgumentException(
                "Transcript research input must satisfy SourceTimestamp <= PublishedAt <= RetrievedAt.",
                nameof(input));
        }
    }
}
