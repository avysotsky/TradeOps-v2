namespace TradeOps.Application.Models;

public sealed record EarningsTranscriptResearchContext(
    InstrumentReference Instrument,
    string FiscalPeriod,
    string EarningsEventAssociationId,
    string? IssuerId = null);

public sealed record EarningsTranscriptParticipant(
    string ParticipantId,
    string DisplayName,
    string? Role = null,
    string? Organization = null);

public sealed record EarningsTranscriptSegment(
    int Sequence,
    string DocFlowSegmentId,
    string? ParticipantId,
    string Text);

public sealed record EarningsTranscriptResearchInput(
    InstrumentReference Instrument,
    string FiscalPeriod,
    string EarningsEventAssociationId,
    string Title,
    DateTimeOffset PublishedAt,
    ResearchSourceProvenance Provenance,
    string DocFlowDocumentId,
    string DocFlowFingerprint,
    IReadOnlyList<EarningsTranscriptParticipant> Participants,
    IReadOnlyList<EarningsTranscriptSegment> Segments);
