using TradeOps.Domain.Enums;

namespace TradeOps.Api.Contracts;

public sealed record SignalIngressRequestAuditResponse(
    Guid AuditId,
    Guid RequestId,
    DateTimeOffset RequestTimestamp,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? CompletedAt,
    string Method,
    string Path,
    SignalIngressRequestOutcome Outcome,
    int? HttpStatusCode,
    Guid? SignalId,
    Guid? OrderId,
    string? ClientOrderId);
