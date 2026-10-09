using TradeOps.Domain.Enums;

namespace TradeOps.Api.Contracts;

public sealed record TradingSignalOutcomeEventResponse(
    Guid Id,
    Guid SignalId,
    SignalOutcome? PreviousOutcome,
    SignalOutcome Outcome,
    DateTimeOffset OccurredAt,
    IReadOnlyCollection<string> RiskRejectionReasons,
    Guid? OrderId,
    string? ClientOrderId);
