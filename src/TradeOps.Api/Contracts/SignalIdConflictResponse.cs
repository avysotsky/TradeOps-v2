namespace TradeOps.Api.Contracts;

public sealed record SignalIdConflictResponse(
    Guid SignalId,
    string Message,
    IReadOnlyCollection<string> ConflictingFields);
