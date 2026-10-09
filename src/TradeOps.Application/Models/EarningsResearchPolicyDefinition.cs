namespace TradeOps.Application.Models;

public sealed record EarningsResearchPolicyDefinition(
    int SchemaVersion,
    string StrategyId,
    EarningsResearchThresholdDefinition Thresholds,
    EarningsResearchTargetWeightDefinition TargetWeights);

public sealed record EarningsResearchThresholdDefinition(
    decimal RevenueGrowth,
    decimal DilutedEpsGrowth,
    decimal OperatingMarginDelta,
    int MinimumDirectionalSignals);

public sealed record EarningsResearchTargetWeightDefinition(
    decimal Positive,
    decimal Neutral,
    decimal Negative);

public static class EarningsResearchPolicyValidationCodes
{
    public const string InvalidJson =
        "invalid_json";

    public const string UnknownProperty =
        "unknown_property";

    public const string DuplicateProperty =
        "duplicate_property";

    public const string MissingRequiredValue =
        "missing_required_value";

    public const string UnsupportedSchemaVersion =
        "unsupported_schema_version";

    public const string InvalidStrategyId =
        "invalid_strategy_id";

    public const string NegativeThreshold =
        "negative_threshold";

    public const string InvalidMinimumDirectionalSignals =
        "invalid_minimum_directional_signals";

    public const string TargetWeightOutOfRange =
        "target_weight_out_of_range";

    public const string TargetWeightPrecisionExceeded =
        "target_weight_precision_exceeded";
}

public sealed record EarningsResearchPolicyValidationError(
    string Code,
    string Path,
    string Message);

public sealed record EarningsResearchPolicyValidationResult(
    IReadOnlyList<EarningsResearchPolicyValidationError> Errors)
{
    public bool IsValid =>
        Errors.Count == 0;
}

public sealed record EarningsResearchPolicyLoadResult(
    EarningsResearchPolicyDefinition? Definition,
    EarningsResearchPolicyValidationResult Validation,
    string? Fingerprint)
{
    public bool IsValid =>
        Definition is not null &&
        Validation.IsValid;
}
