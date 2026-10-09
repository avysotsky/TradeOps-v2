using System.Text.Json;
using System.Text.Json.Serialization;
using TradeOps.Application.Models;

namespace TradeOps.Application.Services;

public static class DocFlowStructuredEarningsFactsAdapter
{
    public const int SupportedSchemaVersion = 1;
    public const string AmountScale = "base_units";
    public const string MarginScale = "fraction";

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNameCaseInsensitive = false,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling =
                JsonUnmappedMemberHandling.Disallow
        };

    public static EarningsTranscriptFactSet Adapt(
        string structuredExtractionJson,
        EarningsTranscriptResearchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Instrument);

        if (string.IsNullOrWhiteSpace(structuredExtractionJson))
        {
            throw Invalid(
                "Structured DocFlow extraction JSON is required.");
        }

        var evidenceSegmentIds =
            GetEvidenceSegmentIds(input);

        StructuredExtractionResultDto? result;

        try
        {
            result =
                JsonSerializer.Deserialize<StructuredExtractionResultDto>(
                    structuredExtractionJson,
                    JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Structured DocFlow extraction JSON does not match the expected DF-03 transport and earnings schema.",
                exception);
        }

        if (result is null)
        {
            throw Invalid(
                "Structured DocFlow extraction JSON must contain an object.");
        }

        var engine =
            RequireNormalizedText(
                result.Engine,
                "engine");

        if (result.DocumentType is not null)
        {
            var documentType =
                RequireNormalizedText(
                    result.DocumentType,
                    "document_type");

            if (!string.Equals(
                    documentType,
                    "transcript",
                    StringComparison.Ordinal))
            {
                throw Invalid(
                    "document_type must be exactly 'transcript' when supplied.");
            }
        }

        ValidateGenericValidationStatus(
            result.ValidationStatus);

        if (result.Confidence is < 0m or > 1m)
        {
            throw Invalid(
                "confidence must be between 0 and 1 when supplied.");
        }

        if (result.Data is null)
        {
            throw Invalid(
                "data is required.");
        }

        var data =
            result.Data;

        if (data.SchemaVersion != SupportedSchemaVersion)
        {
            throw Invalid(
                $"data.schema_version must be {SupportedSchemaVersion}.");
        }

        var documentId =
            RequireNormalizedText(
                data.DocFlowDocumentId,
                "data.docflow_document_id");

        if (!string.Equals(
                documentId,
                input.DocFlowDocumentId,
                StringComparison.Ordinal))
        {
            throw Invalid(
                "data.docflow_document_id does not match the transcript research input.");
        }

        if (!IsLowercaseSha256(
                data.DocFlowFingerprint))
        {
            throw Invalid(
                "data.docflow_fingerprint must be exactly 64 lowercase hexadecimal characters.");
        }

        if (!string.Equals(
                data.DocFlowFingerprint,
                input.DocFlowFingerprint,
                StringComparison.Ordinal))
        {
            throw Invalid(
                "data.docflow_fingerprint does not match the transcript research input.");
        }

        var currency =
            RequireNormalizedText(
                data.Currency,
                "data.currency");

        if (!string.Equals(
                currency,
                currency.ToUpperInvariant(),
                StringComparison.Ordinal))
        {
            throw Invalid(
                "data.currency must already be normalized to uppercase.");
        }

        if (!string.Equals(
                currency,
                input.Instrument.Currency,
                StringComparison.Ordinal))
        {
            throw Invalid(
                "data.currency does not exactly match Instrument.Currency.");
        }

        if (!string.Equals(
                data.AmountScale,
                AmountScale,
                StringComparison.Ordinal))
        {
            throw Invalid(
                $"data.amount_scale must be exactly '{AmountScale}'.");
        }

        if (!string.Equals(
                data.MarginScale,
                MarginScale,
                StringComparison.Ordinal))
        {
            throw Invalid(
                $"data.margin_scale must be exactly '{MarginScale}'.");
        }

        if (data.Facts is null)
        {
            throw Invalid(
                "data.facts is required.");
        }

        if (data.Guidance is null)
        {
            throw Invalid(
                "data.guidance is required.");
        }

        var revenue =
            CreateNumericFact(
                data.Facts.Revenue,
                "data.facts.revenue",
                evidenceSegmentIds);

        var dilutedEps =
            CreateNumericFact(
                data.Facts.DilutedEps,
                "data.facts.diluted_eps",
                evidenceSegmentIds);

        var netIncome =
            CreateNumericFact(
                data.Facts.NetIncome,
                "data.facts.net_income",
                evidenceSegmentIds);

        var grossMargin =
            CreateNumericFact(
                data.Facts.GrossMargin,
                "data.facts.gross_margin",
                evidenceSegmentIds);

        var operatingMargin =
            CreateNumericFact(
                data.Facts.OperatingMargin,
                "data.facts.operating_margin",
                evidenceSegmentIds);

        var guidance =
            CreateGuidance(
                data.Guidance,
                evidenceSegmentIds);

        if (revenue is null
            && dilutedEps is null
            && netIncome is null
            && grossMargin is null
            && operatingMargin is null
            && guidance is null)
        {
            throw Invalid(
                "Structured earnings extraction must contain at least one supported fact or guidance item.");
        }

        return new EarningsTranscriptFactSet(
            SupportedSchemaVersion,
            engine,
            documentId,
            data.DocFlowFingerprint!,
            currency,
            result.Confidence,
            revenue,
            dilutedEps,
            netIncome,
            grossMargin,
            operatingMargin,
            guidance);
    }

    private static HashSet<string> GetEvidenceSegmentIds(
        EarningsTranscriptResearchInput input)
    {
        if (input.Segments is null
            || input.Segments.Count == 0)
        {
            throw new ArgumentException(
                "Transcript research input must contain at least one segment.",
                nameof(input));
        }

        var segmentIds =
            new HashSet<string>(
                StringComparer.Ordinal);

        foreach (var segment in input.Segments)
        {
            if (segment is null
                || string.IsNullOrWhiteSpace(
                    segment.DocFlowSegmentId)
                || !string.Equals(
                    segment.DocFlowSegmentId,
                    segment.DocFlowSegmentId.Trim(),
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Transcript research input contains an invalid DocFlow segment ID.",
                    nameof(input));
            }

            if (!segmentIds.Add(
                    segment.DocFlowSegmentId))
            {
                throw new ArgumentException(
                    $"Transcript research input contains duplicate DocFlow segment ID '{segment.DocFlowSegmentId}'.",
                    nameof(input));
            }
        }

        return segmentIds;
    }

    private static EarningsTranscriptNumericFact? CreateNumericFact(
        NumericFactDto? fact,
        string fieldName,
        IReadOnlySet<string> evidenceSegmentIds)
    {
        if (fact is null)
        {
            return null;
        }

        if (!fact.Value.HasValue)
        {
            throw Invalid(
                $"{fieldName}.value is required.");
        }

        return new EarningsTranscriptNumericFact(
            fact.Value.Value,
            ValidateEvidence(
                fact.EvidenceSegmentIds,
                fieldName,
                evidenceSegmentIds));
    }

    private static EarningsTranscriptGuidanceFactSet? CreateGuidance(
        GuidanceDto guidance,
        IReadOnlySet<string> evidenceSegmentIds)
    {
        var direction =
            CreateDirectionFact(
                guidance.Direction,
                "data.guidance.direction",
                evidenceSegmentIds);

        var revenueLow =
            CreateNumericFact(
                guidance.RevenueLow,
                "data.guidance.revenue_low",
                evidenceSegmentIds);

        var revenueHigh =
            CreateNumericFact(
                guidance.RevenueHigh,
                "data.guidance.revenue_high",
                evidenceSegmentIds);

        var dilutedEpsLow =
            CreateNumericFact(
                guidance.DilutedEpsLow,
                "data.guidance.diluted_eps_low",
                evidenceSegmentIds);

        var dilutedEpsHigh =
            CreateNumericFact(
                guidance.DilutedEpsHigh,
                "data.guidance.diluted_eps_high",
                evidenceSegmentIds);

        ValidateRange(
            revenueLow,
            revenueHigh,
            "data.guidance.revenue_low",
            "data.guidance.revenue_high");

        ValidateRange(
            dilutedEpsLow,
            dilutedEpsHigh,
            "data.guidance.diluted_eps_low",
            "data.guidance.diluted_eps_high");

        if (direction is null
            && revenueLow is null
            && revenueHigh is null
            && dilutedEpsLow is null
            && dilutedEpsHigh is null)
        {
            return null;
        }

        return new EarningsTranscriptGuidanceFactSet(
            direction,
            revenueLow,
            revenueHigh,
            dilutedEpsLow,
            dilutedEpsHigh);
    }

    private static EarningsTranscriptGuidanceDirectionFact?
        CreateDirectionFact(
            DirectionFactDto? fact,
            string fieldName,
            IReadOnlySet<string> evidenceSegmentIds)
    {
        if (fact is null)
        {
            return null;
        }

        var value =
            RequireNormalizedText(
                fact.Value,
                $"{fieldName}.value");

        var direction =
            value switch
            {
                "lowered" =>
                    EarningsGuidanceDirection.Lowered,
                "maintained" =>
                    EarningsGuidanceDirection.Maintained,
                "raised" =>
                    EarningsGuidanceDirection.Raised,
                _ =>
                    throw Invalid(
                        $"{fieldName}.value must be one of: lowered, maintained, raised.")
            };

        return new EarningsTranscriptGuidanceDirectionFact(
            direction,
            ValidateEvidence(
                fact.EvidenceSegmentIds,
                fieldName,
                evidenceSegmentIds));
    }

    private static IReadOnlyList<string> ValidateEvidence(
        List<string?>? evidence,
        string fieldName,
        IReadOnlySet<string> knownSegmentIds)
    {
        if (evidence is null
            || evidence.Count == 0)
        {
            throw Invalid(
                $"{fieldName}.evidence_segment_ids must contain at least one segment ID.");
        }

        var unique =
            new HashSet<string>(
                StringComparer.Ordinal);

        var validated =
            new string[evidence.Count];

        for (var index = 0;
             index < evidence.Count;
             index++)
        {
            var segmentId =
                RequireNormalizedText(
                    evidence[index],
                    $"{fieldName}.evidence_segment_ids[{index}]");

            if (!unique.Add(
                    segmentId))
            {
                throw Invalid(
                    $"{fieldName}.evidence_segment_ids contains duplicate segment ID '{segmentId}'.");
            }

            if (!knownSegmentIds.Contains(
                    segmentId))
            {
                throw Invalid(
                    $"{fieldName}.evidence_segment_ids references unknown segment ID '{segmentId}'.");
            }

            validated[index] =
                segmentId;
        }

        return validated;
    }

    private static void ValidateRange(
        EarningsTranscriptNumericFact? low,
        EarningsTranscriptNumericFact? high,
        string lowFieldName,
        string highFieldName)
    {
        if (low is not null
            && high is not null
            && low.Value > high.Value)
        {
            throw Invalid(
                $"{lowFieldName}.value must be less than or equal to {highFieldName}.value.");
        }
    }

    private static void ValidateGenericValidationStatus(
        string? validationStatus)
    {
        if (validationStatus is null)
        {
            return;
        }

        if (string.Equals(
                validationStatus,
                "invalid",
                StringComparison.Ordinal)
            || string.Equals(
                validationStatus,
                "incomplete",
                StringComparison.Ordinal))
        {
            throw Invalid(
                $"validation_status '{validationStatus}' is not acceptable for earnings consumption.");
        }

        if (!string.Equals(
                validationStatus,
                "valid",
                StringComparison.Ordinal))
        {
            throw Invalid(
                "validation_status must be valid, invalid, incomplete, or null.");
        }
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

    private static bool IsLowercaseSha256(
        string? value)
    {
        if (value is null
            || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsDigit(character)
                && (character < 'a'
                    || character > 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static InvalidDataException Invalid(
        string message) =>
        new(message);

    private sealed class StructuredExtractionResultDto
    {
        [JsonPropertyName("engine")]
        public string? Engine { get; init; }

        [JsonPropertyName("document_type")]
        public string? DocumentType { get; init; }

        [JsonPropertyName("data")]
        public EarningsDataDto? Data { get; init; }

        [JsonPropertyName("confidence")]
        public decimal? Confidence { get; init; }

        [JsonPropertyName("validation_status")]
        public string? ValidationStatus { get; init; }

        [JsonPropertyName("validation")]
        public JsonElement? Validation { get; init; }
    }

    private sealed class EarningsDataDto
    {
        [JsonPropertyName("schema_version")]
        public int? SchemaVersion { get; init; }

        [JsonPropertyName("docflow_document_id")]
        public string? DocFlowDocumentId { get; init; }

        [JsonPropertyName("docflow_fingerprint")]
        public string? DocFlowFingerprint { get; init; }

        [JsonPropertyName("currency")]
        public string? Currency { get; init; }

        [JsonPropertyName("amount_scale")]
        public string? AmountScale { get; init; }

        [JsonPropertyName("margin_scale")]
        public string? MarginScale { get; init; }

        [JsonPropertyName("facts")]
        public FactsDto? Facts { get; init; }

        [JsonPropertyName("guidance")]
        public GuidanceDto? Guidance { get; init; }
    }

    private sealed class FactsDto
    {
        [JsonPropertyName("revenue")]
        public NumericFactDto? Revenue { get; init; }

        [JsonPropertyName("diluted_eps")]
        public NumericFactDto? DilutedEps { get; init; }

        [JsonPropertyName("net_income")]
        public NumericFactDto? NetIncome { get; init; }

        [JsonPropertyName("gross_margin")]
        public NumericFactDto? GrossMargin { get; init; }

        [JsonPropertyName("operating_margin")]
        public NumericFactDto? OperatingMargin { get; init; }
    }

    private sealed class GuidanceDto
    {
        [JsonPropertyName("direction")]
        public DirectionFactDto? Direction { get; init; }

        [JsonPropertyName("revenue_low")]
        public NumericFactDto? RevenueLow { get; init; }

        [JsonPropertyName("revenue_high")]
        public NumericFactDto? RevenueHigh { get; init; }

        [JsonPropertyName("diluted_eps_low")]
        public NumericFactDto? DilutedEpsLow { get; init; }

        [JsonPropertyName("diluted_eps_high")]
        public NumericFactDto? DilutedEpsHigh { get; init; }
    }

    private sealed class NumericFactDto
    {
        [JsonPropertyName("value")]
        public decimal? Value { get; init; }

        [JsonPropertyName("evidence_segment_ids")]
        public List<string?>? EvidenceSegmentIds { get; init; }
    }

    private sealed class DirectionFactDto
    {
        [JsonPropertyName("value")]
        public string? Value { get; init; }

        [JsonPropertyName("evidence_segment_ids")]
        public List<string?>? EvidenceSegmentIds { get; init; }
    }
}
