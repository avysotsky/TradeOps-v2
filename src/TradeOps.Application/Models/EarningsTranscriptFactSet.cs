namespace TradeOps.Application.Models;

public sealed record EarningsTranscriptNumericFact(
    decimal Value,
    IReadOnlyList<string> EvidenceSegmentIds);

public sealed record EarningsTranscriptGuidanceDirectionFact(
    EarningsGuidanceDirection Direction,
    IReadOnlyList<string> EvidenceSegmentIds);

public sealed record EarningsTranscriptGuidanceFactSet(
    EarningsTranscriptGuidanceDirectionFact? Direction = null,
    EarningsTranscriptNumericFact? RevenueLow = null,
    EarningsTranscriptNumericFact? RevenueHigh = null,
    EarningsTranscriptNumericFact? DilutedEpsLow = null,
    EarningsTranscriptNumericFact? DilutedEpsHigh = null);

public sealed record EarningsTranscriptFactSet(
    int SchemaVersion,
    string ExtractionEngine,
    string DocFlowDocumentId,
    string DocFlowFingerprint,
    string Currency,
    decimal? ExtractionConfidence = null,
    EarningsTranscriptNumericFact? Revenue = null,
    EarningsTranscriptNumericFact? DilutedEps = null,
    EarningsTranscriptNumericFact? NetIncome = null,
    EarningsTranscriptNumericFact? GrossMargin = null,
    EarningsTranscriptNumericFact? OperatingMargin = null,
    EarningsTranscriptGuidanceFactSet? Guidance = null);
