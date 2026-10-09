using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Interfaces;

public interface ISignalIngressRequestAuditRepository
{
    Task<Guid> StartAsync(
        Guid requestId,
        DateTimeOffset requestTimestamp,
        DateTimeOffset receivedAt,
        string method,
        string path,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        Guid auditId,
        SignalIngressRequestOutcome outcome,
        int httpStatusCode,
        Guid? signalId = null,
        Guid? orderId = null,
        string? clientOrderId = null,
        CancellationToken cancellationToken = default);

    Task CompleteIfPendingAsync(
        Guid auditId,
        SignalIngressRequestOutcome outcome,
        int httpStatusCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<SignalIngressRequestAudit>> GetByRequestIdAsync(
        Guid requestId,
        CancellationToken cancellationToken = default);
}
