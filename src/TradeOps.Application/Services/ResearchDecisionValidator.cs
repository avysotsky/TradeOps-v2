using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Services;

public static class ResearchDecisionValidator
{
    private const int MaxDecisionIdLength = 160;
    private const int MaxStrategyIdLength = 120;
    private const int MaxSymbolLength = 64;
    private const int MaxCurrencyLength = 12;
    private const int MaxVenueInstrumentIdLength = 128;
    private const int MaxExchangeLength = 64;
    private const int MaxSourceEventIdLength = 200;
    private const int MaxReasonLength = 2000;
    private const int MaxMetadataItems = 32;
    private const int MaxMetadataKeyLength = 100;
    private const int MaxMetadataValueLength = 1000;
    private const int TargetWeightScale = 8;
    private const int ConfidenceScale = 6;

    public static ResearchDecisionValidationResult Validate(
        ResearchDecision? decision)
    {
        var errors =
            new Dictionary<string, List<string>>(
                StringComparer.Ordinal);

        if (decision is null)
        {
            Add(
                errors,
                nameof(ResearchDecision),
                "Research decision is required.");

            return Result(
                null,
                errors);
        }

        ValidateRequiredText(
            errors,
            nameof(decision.DecisionId),
            decision.DecisionId,
            MaxDecisionIdLength);

        ValidateRequiredText(
            errors,
            nameof(decision.StrategyId),
            decision.StrategyId,
            MaxStrategyIdLength);

        ValidateInstrument(
            errors,
            decision.Instrument);

        if (!Enum.IsDefined(
                typeof(ResearchDecisionAction),
                decision.Action)
            || decision.Action ==
               ResearchDecisionAction.Unknown)
        {
            Add(
                errors,
                nameof(decision.Action),
                "Action must identify a supported non-Unknown ResearchDecisionAction value.");
        }

        if (decision.GeneratedAt == default)
        {
            Add(
                errors,
                nameof(decision.GeneratedAt),
                "GeneratedAt is required.");
        }

        if (decision.ValidUntil.HasValue
            && decision.GeneratedAt != default
            && decision.ValidUntil.Value <= decision.GeneratedAt)
        {
            Add(
                errors,
                nameof(decision.ValidUntil),
                "ValidUntil must be later than GeneratedAt.");
        }

        ValidateTargetWeight(
            errors,
            decision);

        if (decision.Confidence.HasValue)
        {
            if (decision.Confidence.Value is < 0m or > 1m)
            {
                Add(
                    errors,
                    nameof(decision.Confidence),
                    "Confidence must be between 0 and 1.");
            }
            else if (GetDecimalScale(
                         decision.Confidence.Value)
                     > ConfidenceScale)
            {
                Add(
                    errors,
                    nameof(decision.Confidence),
                    $"Confidence must have at most {ConfidenceScale} decimal places.");
            }
        }

        ValidateOptionalText(
            errors,
            nameof(decision.SourceEventId),
            decision.SourceEventId,
            MaxSourceEventIdLength);

        ValidateOptionalText(
            errors,
            nameof(decision.Reason),
            decision.Reason,
            MaxReasonLength);

        ValidateMetadata(
            errors,
            decision.Metadata);

        if (errors.Count > 0)
        {
            return Result(
                null,
                errors);
        }

        return Result(
            Normalize(decision),
            errors);
    }

    private static void ValidateInstrument(
        IDictionary<string, List<string>> errors,
        InstrumentReference? instrument)
    {
        if (instrument is null)
        {
            Add(
                errors,
                nameof(ResearchDecision.Instrument),
                "Instrument is required.");
            return;
        }

        ValidateRequiredText(
            errors,
            "Instrument.Symbol",
            instrument.Symbol,
            MaxSymbolLength);

        if (!Enum.IsDefined(
                typeof(AssetClass),
                instrument.AssetClass)
            || instrument.AssetClass == AssetClass.Unknown)
        {
            Add(
                errors,
                "Instrument.AssetClass",
                "AssetClass must identify a supported non-Unknown asset class.");
        }

        ValidateOptionalText(
            errors,
            "Instrument.Currency",
            instrument.Currency,
            MaxCurrencyLength);

        ValidateOptionalText(
            errors,
            "Instrument.VenueInstrumentId",
            instrument.VenueInstrumentId,
            MaxVenueInstrumentIdLength);

        ValidateOptionalText(
            errors,
            "Instrument.Exchange",
            instrument.Exchange,
            MaxExchangeLength);
    }

    private static void ValidateTargetWeight(
        IDictionary<string, List<string>> errors,
        ResearchDecision decision)
    {
        if (decision.Action ==
            ResearchDecisionAction.SetTargetWeight)
        {
            if (!decision.TargetWeight.HasValue)
            {
                Add(
                    errors,
                    nameof(decision.TargetWeight),
                    "TargetWeight is required for SetTargetWeight.");
                return;
            }

            if (GetDecimalScale(
                    decision.TargetWeight.Value)
                > TargetWeightScale)
            {
                Add(
                    errors,
                    nameof(decision.TargetWeight),
                    $"TargetWeight must have at most {TargetWeightScale} decimal places.");
            }

            return;
        }

        if (decision.TargetWeight.HasValue)
        {
            Add(
                errors,
                nameof(decision.TargetWeight),
                "TargetWeight is only valid for SetTargetWeight.");
        }
    }

    private static void ValidateMetadata(
        IDictionary<string, List<string>> errors,
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
        {
            return;
        }

        if (metadata.Count > MaxMetadataItems)
        {
            Add(
                errors,
                nameof(ResearchDecision.Metadata),
                $"Metadata must not contain more than {MaxMetadataItems} items.");
        }

        foreach (var item in metadata)
        {
            if (string.IsNullOrWhiteSpace(item.Key))
            {
                Add(
                    errors,
                    nameof(ResearchDecision.Metadata),
                    "Metadata keys must not be blank.");
                continue;
            }

            if (item.Key.Trim().Length >
                MaxMetadataKeyLength)
            {
                Add(
                    errors,
                    nameof(ResearchDecision.Metadata),
                    $"Metadata keys must not exceed {MaxMetadataKeyLength} characters.");
            }

            if (item.Value is null)
            {
                Add(
                    errors,
                    nameof(ResearchDecision.Metadata),
                    "Metadata values must not be null.");
            }
            else if (item.Value.Length >
                     MaxMetadataValueLength)
            {
                Add(
                    errors,
                    nameof(ResearchDecision.Metadata),
                    $"Metadata values must not exceed {MaxMetadataValueLength} characters.");
            }
        }
    }

    private static ResearchDecision Normalize(
        ResearchDecision decision)
    {
        var instrument =
            decision.Instrument with
            {
                Symbol =
                    decision.Instrument.Symbol
                        .Trim()
                        .ToUpperInvariant(),
                Currency =
                    NormalizeUpper(
                        decision.Instrument.Currency),
                VenueInstrumentId =
                    NormalizeOptional(
                        decision.Instrument.VenueInstrumentId),
                Exchange =
                    NormalizeUpper(
                        decision.Instrument.Exchange)
            };

        IReadOnlyDictionary<string, string>? metadata =
            decision.Metadata is null
                ? null
                : decision.Metadata.ToDictionary(
                    item => item.Key.Trim(),
                    item => item.Value.Trim(),
                    StringComparer.Ordinal);

        return decision with
        {
            DecisionId =
                decision.DecisionId.Trim(),
            StrategyId =
                decision.StrategyId.Trim(),
            Instrument = instrument,
            SourceEventId =
                NormalizeOptional(
                    decision.SourceEventId),
            Reason =
                NormalizeOptional(
                    decision.Reason),
            GeneratedAt =
                decision.GeneratedAt.ToUniversalTime(),
            ValidUntil =
                decision.ValidUntil?.ToUniversalTime(),
            Metadata = metadata
        };
    }

    private static string? NormalizeOptional(
        string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static string? NormalizeUpper(
        string? value) =>
        NormalizeOptional(value)
            ?.ToUpperInvariant();

    private static void ValidateRequiredText(
        IDictionary<string, List<string>> errors,
        string field,
        string? value,
        int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Add(
                errors,
                field,
                $"{field} is required.");
            return;
        }

        if (value.Trim().Length > maxLength)
        {
            Add(
                errors,
                field,
                $"{field} must not exceed {maxLength} characters.");
        }
    }

    private static void ValidateOptionalText(
        IDictionary<string, List<string>> errors,
        string field,
        string? value,
        int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Trim().Length > maxLength)
        {
            Add(
                errors,
                field,
                $"{field} must not exceed {maxLength} characters.");
        }
    }

    private static int GetDecimalScale(
        decimal value) =>
        (decimal.GetBits(value)[3] >> 16) & 0xFF;

    private static ResearchDecisionValidationResult Result(
        ResearchDecision? normalizedDecision,
        IDictionary<string, List<string>> errors) =>
        new(
            normalizedDecision,
            errors.ToDictionary(
                item => item.Key,
                item => item.Value.ToArray(),
                StringComparer.Ordinal));

    private static void Add(
        IDictionary<string, List<string>> errors,
        string field,
        string message)
    {
        if (!errors.TryGetValue(
                field,
                out var messages))
        {
            messages = [];
            errors[field] = messages;
        }

        messages.Add(message);
    }
}
