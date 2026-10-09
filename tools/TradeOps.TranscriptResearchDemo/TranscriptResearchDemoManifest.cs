using System.Text.Json;
using System.Text.Json.Serialization;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.TranscriptResearchDemo;

public sealed record TranscriptResearchDemoArtifactBundle(
    string NormalizedDocumentPath,
    string StructuredExtractionPath,
    EarningsTranscriptResearchContext Context);

public sealed record TranscriptResearchDemoResolvedManifest(
    int SchemaVersion,
    InstrumentReference Instrument,
    string IssuerId,
    TranscriptResearchDemoArtifactBundle Prior,
    TranscriptResearchDemoArtifactBundle Current);

public static class TranscriptResearchDemoManifestLoader
{
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive =
                false,
            AllowTrailingCommas =
                false,
            ReadCommentHandling =
                JsonCommentHandling.Disallow,
            UnmappedMemberHandling =
                JsonUnmappedMemberHandling.Disallow
        };

    public static TranscriptResearchDemoResolvedManifest Load(
        string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(
                manifestPath))
        {
            throw Invalid(
                "Manifest path is required.");
        }

        var fullManifestPath =
            Path.GetFullPath(
                manifestPath);

        if (!File.Exists(
                fullManifestPath))
        {
            throw new FileNotFoundException(
                "Manifest file was not found.",
                fullManifestPath);
        }

        string json;

        try
        {
            json =
                File.ReadAllText(
                    fullManifestPath);
        }
        catch (Exception exception)
            when (exception is IOException
                  or UnauthorizedAccessException)
        {
            throw Invalid(
                $"Manifest file could not be read: {exception.Message}");
        }

        ManifestDto? dto;

        try
        {
            dto =
                JsonSerializer.Deserialize<ManifestDto>(
                    json,
                    JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Manifest JSON does not match the strict VS-08 transport schema.",
                exception);
        }

        if (dto is null)
        {
            throw Invalid(
                "Manifest JSON must contain an object.");
        }

        if (dto.SchemaVersion !=
            SupportedSchemaVersion)
        {
            throw Invalid(
                $"schemaVersion must be exactly {SupportedSchemaVersion}.");
        }

        if (dto.Instrument is null)
        {
            throw Invalid(
                "instrument is required.");
        }

        var symbol =
            RequireNormalized(
                dto.Instrument.Symbol,
                "instrument.symbol");
        var assetClassText =
            RequireNormalized(
                dto.Instrument.AssetClass,
                "instrument.assetClass");
        var currency =
            RequireNormalized(
                dto.Instrument.Currency,
                "instrument.currency");

        if (!string.Equals(
                currency,
                currency.ToUpperInvariant(),
                StringComparison.Ordinal))
        {
            throw Invalid(
                "instrument.currency must already be normalized to uppercase.");
        }

        if (!Enum.TryParse<AssetClass>(
                assetClassText,
                ignoreCase: false,
                out var assetClass)
            || !Enum.IsDefined(
                assetClass)
            || assetClass ==
               AssetClass.Unknown)
        {
            throw Invalid(
                "instrument.assetClass must map exactly to a supported non-Unknown AssetClass.");
        }

        var venueInstrumentId =
            ValidateOptionalNormalized(
                dto.Instrument.VenueInstrumentId,
                "instrument.venueInstrumentId");
        var exchange =
            ValidateOptionalNormalized(
                dto.Instrument.Exchange,
                "instrument.exchange");
        var issuerId =
            RequireNormalized(
                dto.IssuerId,
                "issuerId");

        var instrument =
            new InstrumentReference(
                symbol,
                assetClass,
                currency,
                venueInstrumentId,
                exchange);

        var manifestDirectory =
            Path.GetDirectoryName(
                fullManifestPath)
            ?? throw Invalid(
                "Manifest directory could not be resolved.");

        var prior =
            ResolveBundle(
                dto.Prior,
                "prior",
                manifestDirectory,
                instrument,
                issuerId);
        var current =
            ResolveBundle(
                dto.Current,
                "current",
                manifestDirectory,
                instrument,
                issuerId);

        return new TranscriptResearchDemoResolvedManifest(
            dto.SchemaVersion.Value,
            instrument,
            issuerId,
            prior,
            current);
    }

    private static TranscriptResearchDemoArtifactBundle ResolveBundle(
        ArtifactBundleDto? dto,
        string fieldName,
        string manifestDirectory,
        InstrumentReference instrument,
        string issuerId)
    {
        if (dto is null)
        {
            throw Invalid(
                $"{fieldName} is required.");
        }

        var normalizedDocument =
            ResolveArtifactPath(
                manifestDirectory,
                RequireNormalized(
                    dto.NormalizedDocument,
                    $"{fieldName}.normalizedDocument"),
                $"{fieldName}.normalizedDocument");

        var structuredExtraction =
            ResolveArtifactPath(
                manifestDirectory,
                RequireNormalized(
                    dto.StructuredExtraction,
                    $"{fieldName}.structuredExtraction"),
                $"{fieldName}.structuredExtraction");

        var fiscalPeriod =
            RequireNormalized(
                dto.FiscalPeriod,
                $"{fieldName}.fiscalPeriod");
        var eventId =
            RequireNormalized(
                dto.EarningsEventAssociationId,
                $"{fieldName}.earningsEventAssociationId");

        return new TranscriptResearchDemoArtifactBundle(
            normalizedDocument,
            structuredExtraction,
            new EarningsTranscriptResearchContext(
                instrument,
                fiscalPeriod,
                eventId,
                issuerId));
    }

    private static string ResolveArtifactPath(
        string manifestDirectory,
        string relativePath,
        string fieldName)
    {
        if (Path.IsPathRooted(
                relativePath))
        {
            throw Invalid(
                $"{fieldName} must be relative to the manifest directory.");
        }

        var candidate =
            Path.GetFullPath(
                Path.Combine(
                    manifestDirectory,
                    relativePath));
        var relative =
            Path.GetRelativePath(
                manifestDirectory,
                candidate);

        if (string.Equals(
                relative,
                "..",
                StringComparison.Ordinal)
            || relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            || Path.IsPathRooted(
                relative))
        {
            throw Invalid(
                $"{fieldName} must resolve inside the manifest directory.");
        }

        return candidate;
    }

    private static string RequireNormalized(
        string? value,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(
                value))
        {
            throw Invalid(
                $"{fieldName} is required.");
        }

        if (!string.Equals(
                value,
                value.Trim(),
                StringComparison.Ordinal))
        {
            throw Invalid(
                $"{fieldName} must already be normalized.");
        }

        return value;
    }

    private static string? ValidateOptionalNormalized(
        string? value,
        string fieldName)
    {
        if (value is null)
        {
            return null;
        }

        return RequireNormalized(
            value,
            fieldName);
    }

    private static InvalidDataException Invalid(
        string message) =>
        new(message);

    private sealed class ManifestDto
    {
        public int? SchemaVersion { get; init; }

        public InstrumentDto? Instrument { get; init; }

        public string? IssuerId { get; init; }

        public ArtifactBundleDto? Prior { get; init; }

        public ArtifactBundleDto? Current { get; init; }
    }

    private sealed class InstrumentDto
    {
        public string? Symbol { get; init; }

        public string? AssetClass { get; init; }

        public string? Currency { get; init; }

        public string? VenueInstrumentId { get; init; }

        public string? Exchange { get; init; }
    }

    private sealed class ArtifactBundleDto
    {
        public string? NormalizedDocument { get; init; }

        public string? StructuredExtraction { get; init; }

        public string? FiscalPeriod { get; init; }

        public string? EarningsEventAssociationId { get; init; }
    }
}
