using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradeOps.Application.Models;

namespace TradeOps.Application.Services;

public static class EarningsResearchPolicyConfiguration
{
    public const int SupportedSchemaVersion = 1;
    public const int MaximumTargetWeightDecimalPlaces = 8;

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
                JsonUnmappedMemberHandling.Disallow,
            WriteIndented =
                true
        };

    private static readonly HashSet<string> RootProperties =
        new(
            [
                "schemaVersion",
                "strategyId",
                "thresholds",
                "targetWeights"
            ],
            StringComparer.Ordinal);

    private static readonly HashSet<string> ThresholdProperties =
        new(
            [
                "revenueGrowth",
                "dilutedEpsGrowth",
                "operatingMarginDelta",
                "minimumDirectionalSignals"
            ],
            StringComparer.Ordinal);

    private static readonly HashSet<string> TargetWeightProperties =
        new(
            [
                "positive",
                "neutral",
                "negative"
            ],
            StringComparer.Ordinal);

    public static EarningsResearchPolicyLoadResult Load(
        string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Failure(
                EarningsResearchPolicyValidationCodes.InvalidJson,
                "$",
                "Policy JSON is required.");
        }

        JsonDocument document;

        try
        {
            document =
                JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Failure(
                EarningsResearchPolicyValidationCodes.InvalidJson,
                "$",
                "Policy JSON is malformed.");
        }

        using (document)
        {
            var shapeErrors =
                ValidateJsonShape(
                    document.RootElement);

            if (shapeErrors.Count > 0)
            {
                return new EarningsResearchPolicyLoadResult(
                    null,
                    new EarningsResearchPolicyValidationResult(
                        shapeErrors),
                    null);
            }
        }

        PolicyDto? dto;

        try
        {
            dto =
                JsonSerializer.Deserialize<PolicyDto>(
                    json,
                    JsonOptions);
        }
        catch (JsonException exception)
        {
            return Failure(
                EarningsResearchPolicyValidationCodes.InvalidJson,
                exception.Path ?? "$",
                "Policy JSON does not match the expected schema.");
        }

        if (dto is null)
        {
            return Failure(
                EarningsResearchPolicyValidationCodes.InvalidJson,
                "$",
                "Policy JSON must contain a JSON object.");
        }

        var errors =
            ValidateDto(dto);

        if (errors.Count > 0)
        {
            return new EarningsResearchPolicyLoadResult(
                null,
                new EarningsResearchPolicyValidationResult(
                    errors),
                null);
        }

        var definition =
            new EarningsResearchPolicyDefinition(
                dto.SchemaVersion!.Value,
                dto.StrategyId!.Trim(),
                new EarningsResearchThresholdDefinition(
                    dto.Thresholds!.RevenueGrowth!.Value,
                    dto.Thresholds.DilutedEpsGrowth!.Value,
                    dto.Thresholds.OperatingMarginDelta!.Value,
                    dto.Thresholds.MinimumDirectionalSignals!.Value),
                new EarningsResearchTargetWeightDefinition(
                    dto.TargetWeights!.Positive!.Value,
                    dto.TargetWeights.Neutral!.Value,
                    dto.TargetWeights.Negative!.Value));

        return new EarningsResearchPolicyLoadResult(
            definition,
            new EarningsResearchPolicyValidationResult(
                Array.Empty<EarningsResearchPolicyValidationError>()),
            ComputeFingerprintCore(
                definition));
    }

    public static EarningsResearchPolicyValidationResult Validate(
        EarningsResearchPolicyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(
            definition);

        var errors =
            new List<EarningsResearchPolicyValidationError>();

        if (definition.SchemaVersion !=
            SupportedSchemaVersion)
        {
            errors.Add(
                Error(
                    EarningsResearchPolicyValidationCodes
                        .UnsupportedSchemaVersion,
                    "$.schemaVersion",
                    $"SchemaVersion {definition.SchemaVersion} is not supported. Supported version: {SupportedSchemaVersion}."));
        }

        if (string.IsNullOrWhiteSpace(
                definition.StrategyId))
        {
            errors.Add(
                Error(
                    EarningsResearchPolicyValidationCodes
                        .InvalidStrategyId,
                    "$.strategyId",
                    "StrategyId must be non-empty."));
        }

        if (definition.Thresholds is null)
        {
            errors.Add(
                Error(
                    EarningsResearchPolicyValidationCodes
                        .MissingRequiredValue,
                    "$.thresholds",
                    "Thresholds are required."));
        }
        else
        {
            ValidateThreshold(
                definition.Thresholds.RevenueGrowth,
                "$.thresholds.revenueGrowth",
                errors);
            ValidateThreshold(
                definition.Thresholds.DilutedEpsGrowth,
                "$.thresholds.dilutedEpsGrowth",
                errors);
            ValidateThreshold(
                definition.Thresholds.OperatingMarginDelta,
                "$.thresholds.operatingMarginDelta",
                errors);

            if (definition.Thresholds.MinimumDirectionalSignals
                is < 1 or > 3)
            {
                errors.Add(
                    Error(
                        EarningsResearchPolicyValidationCodes
                            .InvalidMinimumDirectionalSignals,
                        "$.thresholds.minimumDirectionalSignals",
                        "MinimumDirectionalSignals must be between 1 and 3."));
            }
        }

        if (definition.TargetWeights is null)
        {
            errors.Add(
                Error(
                    EarningsResearchPolicyValidationCodes
                        .MissingRequiredValue,
                    "$.targetWeights",
                    "TargetWeights are required."));
        }
        else
        {
            ValidateTargetWeight(
                definition.TargetWeights.Positive,
                "$.targetWeights.positive",
                errors);
            ValidateTargetWeight(
                definition.TargetWeights.Neutral,
                "$.targetWeights.neutral",
                errors);
            ValidateTargetWeight(
                definition.TargetWeights.Negative,
                "$.targetWeights.negative",
                errors);
        }

        return new EarningsResearchPolicyValidationResult(
            errors);
    }

    public static EarningsDecisionRuleSettings ToDecisionRuleSettings(
        EarningsResearchPolicyDefinition definition)
    {
        var normalized =
            NormalizeAndEnsureValid(
                definition);

        return new EarningsDecisionRuleSettings(
            normalized.Thresholds.RevenueGrowth,
            normalized.Thresholds.DilutedEpsGrowth,
            normalized.Thresholds.OperatingMarginDelta,
            normalized.Thresholds.MinimumDirectionalSignals);
    }

    public static EarningsTargetWeightPolicy ToTargetWeightPolicy(
        EarningsResearchPolicyDefinition definition)
    {
        var normalized =
            NormalizeAndEnsureValid(
                definition);

        return new EarningsTargetWeightPolicy(
            normalized.TargetWeights.Positive,
            normalized.TargetWeights.Neutral,
            normalized.TargetWeights.Negative);
    }

    public static string Serialize(
        EarningsResearchPolicyDefinition definition)
    {
        var normalized =
            NormalizeAndEnsureValid(
                definition);

        return JsonSerializer.Serialize(
            normalized,
            JsonOptions);
    }

    public static string ComputeFingerprint(
        EarningsResearchPolicyDefinition definition)
    {
        var normalized =
            NormalizeAndEnsureValid(
                definition);

        return ComputeFingerprintCore(
            normalized);
    }

    private static EarningsResearchPolicyDefinition
        NormalizeAndEnsureValid(
            EarningsResearchPolicyDefinition definition)
    {
        var validation =
            Validate(definition);

        if (!validation.IsValid)
        {
            var message =
                string.Join(
                    "; ",
                    validation.Errors.Select(
                        item =>
                            $"{item.Code} at {item.Path}: {item.Message}"));

            throw new ArgumentException(
                $"Earnings research policy is invalid. {message}",
                nameof(definition));
        }

        return definition with
        {
            StrategyId =
                definition.StrategyId.Trim()
        };
    }

    private static List<EarningsResearchPolicyValidationError>
        ValidateJsonShape(
            JsonElement root)
    {
        var errors =
            new List<EarningsResearchPolicyValidationError>();

        if (root.ValueKind !=
            JsonValueKind.Object)
        {
            errors.Add(
                Error(
                    EarningsResearchPolicyValidationCodes.InvalidJson,
                    "$",
                    "Policy JSON root must be an object."));

            return errors;
        }

        ValidateObjectProperties(
            root,
            "$",
            RootProperties,
            errors);

        if (TryGetSingleProperty(
                root,
                "thresholds",
                out var thresholds) &&
            thresholds.ValueKind ==
            JsonValueKind.Object)
        {
            ValidateObjectProperties(
                thresholds,
                "$.thresholds",
                ThresholdProperties,
                errors);
        }

        if (TryGetSingleProperty(
                root,
                "targetWeights",
                out var targetWeights) &&
            targetWeights.ValueKind ==
            JsonValueKind.Object)
        {
            ValidateObjectProperties(
                targetWeights,
                "$.targetWeights",
                TargetWeightProperties,
                errors);
        }

        return errors;
    }

    private static void ValidateObjectProperties(
        JsonElement element,
        string path,
        IReadOnlySet<string> allowed,
        ICollection<EarningsResearchPolicyValidationError> errors)
    {
        var seen =
            new HashSet<string>(
                StringComparer.Ordinal);

        foreach (var property in
                 element.EnumerateObject())
        {
            var propertyPath =
                $"{path}.{property.Name}";

            if (!seen.Add(
                    property.Name))
            {
                errors.Add(
                    Error(
                        EarningsResearchPolicyValidationCodes
                            .DuplicateProperty,
                        propertyPath,
                        $"Property '{property.Name}' must not appear more than once."));

                continue;
            }

            if (!allowed.Contains(
                    property.Name))
            {
                errors.Add(
                    Error(
                        EarningsResearchPolicyValidationCodes
                            .UnknownProperty,
                        propertyPath,
                        $"Property '{property.Name}' is not supported."));
            }
        }
    }

    private static bool TryGetSingleProperty(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        var found =
            false;
        value =
            default;

        foreach (var property in
                 element.EnumerateObject())
        {
            if (!string.Equals(
                    property.Name,
                    propertyName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (found)
            {
                return false;
            }

            value =
                property.Value;
            found =
                true;
        }

        return found;
    }

    private static List<EarningsResearchPolicyValidationError>
        ValidateDto(
            PolicyDto dto)
    {
        var errors =
            new List<EarningsResearchPolicyValidationError>();

        if (!dto.SchemaVersion.HasValue)
        {
            errors.Add(
                Error(
                    EarningsResearchPolicyValidationCodes
                        .MissingRequiredValue,
                    "$.schemaVersion",
                    "SchemaVersion is required."));
        }
        else if (dto.SchemaVersion.Value !=
                 SupportedSchemaVersion)
        {
            errors.Add(
                Error(
                    EarningsResearchPolicyValidationCodes
                        .UnsupportedSchemaVersion,
                    "$.schemaVersion",
                    $"SchemaVersion {dto.SchemaVersion.Value} is not supported. Supported version: {SupportedSchemaVersion}."));
        }

        if (string.IsNullOrWhiteSpace(
                dto.StrategyId))
        {
            errors.Add(
                Error(
                    EarningsResearchPolicyValidationCodes
                        .InvalidStrategyId,
                    "$.strategyId",
                    "StrategyId must be non-empty."));
        }

        if (dto.Thresholds is null)
        {
            errors.Add(
                Error(
                    EarningsResearchPolicyValidationCodes
                        .MissingRequiredValue,
                    "$.thresholds",
                    "Thresholds are required."));
        }
        else
        {
            ValidateRequiredThreshold(
                dto.Thresholds.RevenueGrowth,
                "$.thresholds.revenueGrowth",
                errors);
            ValidateRequiredThreshold(
                dto.Thresholds.DilutedEpsGrowth,
                "$.thresholds.dilutedEpsGrowth",
                errors);
            ValidateRequiredThreshold(
                dto.Thresholds.OperatingMarginDelta,
                "$.thresholds.operatingMarginDelta",
                errors);

            if (!dto.Thresholds.MinimumDirectionalSignals
                .HasValue)
            {
                errors.Add(
                    Error(
                        EarningsResearchPolicyValidationCodes
                            .MissingRequiredValue,
                        "$.thresholds.minimumDirectionalSignals",
                        "MinimumDirectionalSignals is required."));
            }
            else if (dto.Thresholds.MinimumDirectionalSignals
                     .Value is < 1 or > 3)
            {
                errors.Add(
                    Error(
                        EarningsResearchPolicyValidationCodes
                            .InvalidMinimumDirectionalSignals,
                        "$.thresholds.minimumDirectionalSignals",
                        "MinimumDirectionalSignals must be between 1 and 3."));
            }
        }

        if (dto.TargetWeights is null)
        {
            errors.Add(
                Error(
                    EarningsResearchPolicyValidationCodes
                        .MissingRequiredValue,
                    "$.targetWeights",
                    "TargetWeights are required."));
        }
        else
        {
            ValidateRequiredTargetWeight(
                dto.TargetWeights.Positive,
                "$.targetWeights.positive",
                errors);
            ValidateRequiredTargetWeight(
                dto.TargetWeights.Neutral,
                "$.targetWeights.neutral",
                errors);
            ValidateRequiredTargetWeight(
                dto.TargetWeights.Negative,
                "$.targetWeights.negative",
                errors);
        }

        return errors;
    }

    private static void ValidateRequiredThreshold(
        decimal? value,
        string path,
        ICollection<EarningsResearchPolicyValidationError> errors)
    {
        if (!value.HasValue)
        {
            errors.Add(
                Error(
                    EarningsResearchPolicyValidationCodes
                        .MissingRequiredValue,
                    path,
                    "Threshold value is required."));

            return;
        }

        ValidateThreshold(
            value.Value,
            path,
            errors);
    }

    private static void ValidateThreshold(
        decimal value,
        string path,
        ICollection<EarningsResearchPolicyValidationError> errors)
    {
        if (value < 0m)
        {
            errors.Add(
                Error(
                    EarningsResearchPolicyValidationCodes
                        .NegativeThreshold,
                    path,
                    "Research thresholds cannot be negative."));
        }
    }

    private static void ValidateRequiredTargetWeight(
        decimal? value,
        string path,
        ICollection<EarningsResearchPolicyValidationError> errors)
    {
        if (!value.HasValue)
        {
            errors.Add(
                Error(
                    EarningsResearchPolicyValidationCodes
                        .MissingRequiredValue,
                    path,
                    "Target weight is required."));

            return;
        }

        ValidateTargetWeight(
            value.Value,
            path,
            errors);
    }

    private static void ValidateTargetWeight(
        decimal value,
        string path,
        ICollection<EarningsResearchPolicyValidationError> errors)
    {
        if (value is < 0m or > 1m)
        {
            errors.Add(
                Error(
                    EarningsResearchPolicyValidationCodes
                        .TargetWeightOutOfRange,
                    path,
                    "Long-only target weight must be between 0 and 1."));

            return;
        }

        if (GetDecimalScale(value) >
            MaximumTargetWeightDecimalPlaces)
        {
            errors.Add(
                Error(
                    EarningsResearchPolicyValidationCodes
                        .TargetWeightPrecisionExceeded,
                    path,
                    $"Target weight must have at most {MaximumTargetWeightDecimalPlaces} decimal places."));
        }
    }

    private static int GetDecimalScale(
        decimal value)
    {
        var bits =
            decimal.GetBits(value);

        return (bits[3] >> 16) & 0x7F;
    }

    private static string ComputeFingerprintCore(
        EarningsResearchPolicyDefinition definition)
    {
        var canonical =
            string.Join(
                "\n",
                $"schemaVersion={definition.SchemaVersion}",
                $"strategyId={definition.StrategyId.Trim()}",
                $"revenueGrowth={FormatDecimal(definition.Thresholds.RevenueGrowth)}",
                $"dilutedEpsGrowth={FormatDecimal(definition.Thresholds.DilutedEpsGrowth)}",
                $"operatingMarginDelta={FormatDecimal(definition.Thresholds.OperatingMarginDelta)}",
                $"minimumDirectionalSignals={definition.Thresholds.MinimumDirectionalSignals.ToString(CultureInfo.InvariantCulture)}",
                $"positive={FormatDecimal(definition.TargetWeights.Positive)}",
                $"neutral={FormatDecimal(definition.TargetWeights.Neutral)}",
                $"negative={FormatDecimal(definition.TargetWeights.Negative)}");

        var hash =
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    canonical));

        return Convert
            .ToHexString(hash)
            .ToLowerInvariant();
    }

    private static string FormatDecimal(
        decimal value) =>
        value.ToString(
            "G29",
            CultureInfo.InvariantCulture);

    private static EarningsResearchPolicyValidationError Error(
        string code,
        string path,
        string message) =>
        new(
            code,
            path,
            message);

    private static EarningsResearchPolicyLoadResult Failure(
        string code,
        string path,
        string message) =>
        new(
            null,
            new EarningsResearchPolicyValidationResult(
                [
                    Error(
                        code,
                        path,
                        message)
                ]),
            null);

    private sealed class PolicyDto
    {
        public int? SchemaVersion { get; set; }

        public string? StrategyId { get; set; }

        public ThresholdsDto? Thresholds { get; set; }

        public TargetWeightsDto? TargetWeights { get; set; }
    }

    private sealed class ThresholdsDto
    {
        public decimal? RevenueGrowth { get; set; }

        public decimal? DilutedEpsGrowth { get; set; }

        public decimal? OperatingMarginDelta { get; set; }

        public int? MinimumDirectionalSignals { get; set; }
    }

    private sealed class TargetWeightsDto
    {
        public decimal? Positive { get; set; }

        public decimal? Neutral { get; set; }

        public decimal? Negative { get; set; }
    }
}
