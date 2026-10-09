namespace TradeOps.Application.Models;

public sealed record ResearchSourceProvenance(
    string Provider,
    Uri SourceUri,
    DateTimeOffset SourceTimestamp,
    DateTimeOffset RetrievedAt,
    string ExtractionMethod,
    string? SourceDocumentId = null,
    string? IssuerId = null);
