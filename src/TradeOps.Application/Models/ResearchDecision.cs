using TradeOps.Domain.Enums;

namespace TradeOps.Application.Models;

public sealed record ResearchDecision(
    string DecisionId,
    string StrategyId,
    InstrumentReference Instrument,
    ResearchDecisionAction Action,
    DateTimeOffset GeneratedAt,
    decimal? TargetWeight = null,
    decimal? Confidence = null,
    string? SourceEventId = null,
    string? Reason = null,
    DateTimeOffset? ValidUntil = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
