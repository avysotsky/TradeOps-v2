using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradeOps.Application.Models;

namespace TradeOps.Application.Services;

public static class DocFlowEarningsResearchAdapter
{
    public const string ExtractionMethod =
        "docflow-normalized-text-v1";

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNameCaseInsensitive = false,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling =
                JsonUnmappedMemberHandling.Disallow
        };

    public static EarningsTranscriptResearchInput Adapt(
        string normalizedDocumentJson,
        EarningsTranscriptResearchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Instrument);

        var fiscalPeriod =
            RequireContextValue(
                context.FiscalPeriod,
                nameof(context.FiscalPeriod));

        var earningsEventAssociationId =
            RequireContextValue(
                context.EarningsEventAssociationId,
                nameof(context.EarningsEventAssociationId));

        var issuerId =
            ValidateOptionalContextValue(
                context.IssuerId,
                nameof(context.IssuerId));

        if (string.IsNullOrWhiteSpace(normalizedDocumentJson))
        {
            throw Invalid(
                "Normalized DocFlow JSON is required.");
        }

        NormalizedTextDocumentDto? document;

        try
        {
            document =
                JsonSerializer.Deserialize<NormalizedTextDocumentDto>(
                    normalizedDocumentJson,
                    JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Normalized DocFlow JSON does not match the expected DF-02 transport schema.",
                exception);
        }

        if (document is null)
        {
            throw Invalid(
                "Normalized DocFlow JSON must contain an object.");
        }

        var documentId =
            RequireNormalizedText(
                document.DocumentId,
                "document_id");

        var title =
            RequireNormalizedText(
                document.Title,
                "title");

        var documentType =
            RequireNormalizedText(
                document.DocumentType,
                "document_type");

        if (!string.Equals(
                documentType,
                "transcript",
                StringComparison.Ordinal))
        {
            throw Invalid(
                "document_type must be exactly 'transcript'.");
        }

        if (!IsLowercaseSha256(
                document.Fingerprint))
        {
            throw Invalid(
                "fingerprint must be exactly 64 lowercase hexadecimal characters.");
        }

        if (document.Source is null)
        {
            throw Invalid(
                "source is required.");
        }

        var provider =
            RequireNormalizedText(
                document.Source.Provider,
                "source.provider");

        var sourceUriText =
            RequireNormalizedText(
                document.Source.SourceUri,
                "source.source_uri");

        if (!Uri.TryCreate(
                sourceUriText,
                UriKind.Absolute,
                out var sourceUri) ||
            !sourceUri.IsAbsoluteUri)
        {
            throw Invalid(
                "source.source_uri must be an absolute URI.");
        }

        var sourceDocumentId =
            ValidateOptionalNormalizedText(
                document.Source.SourceDocumentId,
                "source.source_document_id");

        var sourceTimestamp =
            ParseRequiredUtcTimestamp(
                document.Source.SourceTimestamp,
                "source.source_timestamp");

        var publishedAt =
            ParseRequiredUtcTimestamp(
                document.Source.PublishedAt,
                "source.published_at");

        var retrievedAt =
            ParseRequiredUtcTimestamp(
                document.Source.RetrievedAt,
                "source.retrieved_at");

        if (sourceTimestamp > publishedAt ||
            publishedAt > retrievedAt)
        {
            throw Invalid(
                "timestamps must satisfy source_timestamp <= published_at <= retrieved_at.");
        }

        if (document.Participants is null)
        {
            throw Invalid(
                "participants is required.");
        }

        var participantIds =
            new HashSet<string>(
                StringComparer.Ordinal);

        var participants =
            new EarningsTranscriptParticipant[
                document.Participants.Count];

        for (var index = 0;
             index < document.Participants.Count;
             index++)
        {
            var participant =
                document.Participants[index];

            if (participant is null)
            {
                throw Invalid(
                    $"participants[{index}] must be an object.");
            }

            var participantId =
                RequireNormalizedText(
                    participant.ParticipantId,
                    $"participants[{index}].participant_id");

            if (!participantIds.Add(
                    participantId))
            {
                throw Invalid(
                    $"participant_id '{participantId}' is duplicated.");
            }

            participants[index] =
                new EarningsTranscriptParticipant(
                    participantId,
                    RequireNormalizedText(
                        participant.DisplayName,
                        $"participants[{index}].display_name"),
                    ValidateOptionalNormalizedText(
                        participant.Role,
                        $"participants[{index}].role"),
                    ValidateOptionalNormalizedText(
                        participant.Organization,
                        $"participants[{index}].organization"));
        }

        if (document.Segments is null ||
            document.Segments.Count == 0)
        {
            throw Invalid(
                "segments must contain at least one transcript segment.");
        }

        var segments =
            new EarningsTranscriptSegment[
                document.Segments.Count];

        var previousSequence = 0;

        for (var index = 0;
             index < document.Segments.Count;
             index++)
        {
            var segment =
                document.Segments[index];

            if (segment is null)
            {
                throw Invalid(
                    $"segments[{index}] must be an object.");
            }

            if (segment.Sequence <= 0)
            {
                throw Invalid(
                    $"segments[{index}].sequence must be positive.");
            }

            if (segment.Sequence <= previousSequence)
            {
                throw Invalid(
                    "segment sequence values must be unique and strictly ordered.");
            }

            previousSequence =
                segment.Sequence;

            var participantId =
                ValidateOptionalNormalizedText(
                    segment.ParticipantId,
                    $"segments[{index}].participant_id");

            if (participantId is not null &&
                !participantIds.Contains(
                    participantId))
            {
                throw Invalid(
                    $"segments[{index}].participant_id references an unknown participant.");
            }

            segments[index] =
                new EarningsTranscriptSegment(
                    segment.Sequence,
                    RequireNormalizedText(
                        segment.SegmentId,
                        $"segments[{index}].segment_id"),
                    participantId,
                    RequireNormalizedText(
                        segment.Text,
                        $"segments[{index}].text"));
        }

        var provenance =
            new ResearchSourceProvenance(
                provider,
                sourceUri,
                sourceTimestamp,
                retrievedAt,
                ExtractionMethod,
                sourceDocumentId,
                issuerId);

        return new EarningsTranscriptResearchInput(
            context.Instrument,
            fiscalPeriod,
            earningsEventAssociationId,
            title,
            publishedAt,
            provenance,
            documentId,
            document.Fingerprint!,
            participants,
            segments);
    }

    private static string RequireContextValue(
        string? value,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"{fieldName} is required.",
                fieldName);
        }

        if (!string.Equals(
                value,
                value.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{fieldName} must not contain surrounding whitespace.",
                fieldName);
        }

        return value;
    }

    private static string? ValidateOptionalContextValue(
        string? value,
        string fieldName)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(
                value,
                value.Trim(),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{fieldName} must be a normalized non-empty value when supplied.",
                fieldName);
        }

        return value;
    }

    private static string RequireNormalizedText(
        string? value,
        string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
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

    private static string? ValidateOptionalNormalizedText(
        string? value,
        string fieldName)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(
                value,
                value.Trim(),
                StringComparison.Ordinal))
        {
            throw Invalid(
                $"{fieldName} must be a normalized non-empty value when supplied.");
        }

        return value;
    }

    private static DateTimeOffset ParseRequiredUtcTimestamp(
        string? value,
        string fieldName)
    {
        var normalized =
            RequireNormalizedText(
                value,
                fieldName);

        if (!HasExplicitOffset(
                normalized) ||
            !DateTimeOffset.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            throw Invalid(
                $"{fieldName} must be an ISO-8601 timestamp with an explicit timezone offset.");
        }

        if (parsed.Offset != TimeSpan.Zero)
        {
            throw Invalid(
                $"{fieldName} must be normalized to UTC.");
        }

        return parsed;
    }

    private static bool HasExplicitOffset(
        string value)
    {
        if (value.EndsWith(
                "Z",
                StringComparison.Ordinal))
        {
            return true;
        }

        if (value.Length < 6)
        {
            return false;
        }

        var sign =
            value[^6];

        return
            (sign == '+' || sign == '-') &&
            char.IsDigit(value[^5]) &&
            char.IsDigit(value[^4]) &&
            value[^3] == ':' &&
            char.IsDigit(value[^2]) &&
            char.IsDigit(value[^1]);
    }

    private static bool IsLowercaseSha256(
        string? value)
    {
        if (value is null ||
            value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsDigit(character) &&
                (character < 'a' ||
                 character > 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static InvalidDataException Invalid(
        string message) =>
        new(message);

    private sealed class NormalizedTextDocumentDto
    {
        [JsonPropertyName("document_id")]
        public string? DocumentId { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("document_type")]
        public string? DocumentType { get; init; }

        [JsonPropertyName("source")]
        public SourceDto? Source { get; init; }

        [JsonPropertyName("participants")]
        public List<ParticipantDto?>? Participants { get; init; }

        [JsonPropertyName("segments")]
        public List<SegmentDto?>? Segments { get; init; }

        [JsonPropertyName("fingerprint")]
        public string? Fingerprint { get; init; }
    }

    private sealed class SourceDto
    {
        [JsonPropertyName("provider")]
        public string? Provider { get; init; }

        [JsonPropertyName("source_uri")]
        public string? SourceUri { get; init; }

        [JsonPropertyName("source_document_id")]
        public string? SourceDocumentId { get; init; }

        [JsonPropertyName("source_timestamp")]
        public string? SourceTimestamp { get; init; }

        [JsonPropertyName("published_at")]
        public string? PublishedAt { get; init; }

        [JsonPropertyName("retrieved_at")]
        public string? RetrievedAt { get; init; }
    }

    private sealed class ParticipantDto
    {
        [JsonPropertyName("participant_id")]
        public string? ParticipantId { get; init; }

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; init; }

        [JsonPropertyName("role")]
        public string? Role { get; init; }

        [JsonPropertyName("organization")]
        public string? Organization { get; init; }
    }

    private sealed class SegmentDto
    {
        [JsonPropertyName("sequence")]
        public int Sequence { get; init; }

        [JsonPropertyName("participant_id")]
        public string? ParticipantId { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("segment_id")]
        public string? SegmentId { get; init; }
    }
}
