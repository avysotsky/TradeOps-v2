using Microsoft.EntityFrameworkCore;
using TradeOps.Application.Interfaces;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Persistence.Repositories;

public sealed class EfSignalIngressRequestAuditRepository(
    TradeOpsDbContext dbContext) : ISignalIngressRequestAuditRepository
{
    public async Task<Guid> StartAsync(
        Guid requestId,
        DateTimeOffset requestTimestamp,
        DateTimeOffset receivedAt,
        string method,
        string path,
        CancellationToken cancellationToken = default)
    {
        var audit = new SignalIngressRequestAudit
        {
            Id = Guid.NewGuid(),
            RequestId = requestId,
            RequestTimestamp = requestTimestamp,
            ReceivedAt = receivedAt,
            Method = method,
            Path = path,
            Outcome = SignalIngressRequestOutcome.Received
        };

        dbContext.SignalIngressRequestAudits.Add(audit);
        await dbContext.SaveChangesAsync(cancellationToken);
        return audit.Id;
    }

    public async Task CompleteAsync(
        Guid auditId,
        SignalIngressRequestOutcome outcome,
        int httpStatusCode,
        Guid? signalId = null,
        Guid? orderId = null,
        string? clientOrderId = null,
        CancellationToken cancellationToken = default)
    {
        var audit = await dbContext.SignalIngressRequestAudits
            .SingleOrDefaultAsync(
                item => item.Id == auditId,
                cancellationToken);

        if (audit is null)
        {
            throw new InvalidOperationException(
                $"Signal ingress audit '{auditId}' was not found.");
        }

        audit.Outcome = outcome;
        audit.HttpStatusCode = httpStatusCode;
        audit.SignalId = signalId;
        audit.OrderId = orderId;
        audit.ClientOrderId = clientOrderId;
        audit.CompletedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task CompleteIfPendingAsync(
        Guid auditId,
        SignalIngressRequestOutcome outcome,
        int httpStatusCode,
        CancellationToken cancellationToken = default)
    {
        var audit = await dbContext.SignalIngressRequestAudits
            .SingleOrDefaultAsync(
                item => item.Id == auditId,
                cancellationToken);

        if (audit is null
            || audit.Outcome != SignalIngressRequestOutcome.Received)
        {
            return;
        }

        audit.Outcome = outcome;
        audit.HttpStatusCode = httpStatusCode;
        audit.CompletedAt = DateTimeOffset.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<SignalIngressRequestAudit>> GetByRequestIdAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.SignalIngressRequestAudits
            .AsNoTracking()
            .Where(item => item.RequestId == requestId)
            .OrderBy(item => item.ReceivedAt)
            .ThenBy(item => item.Id)
            .ToArrayAsync(cancellationToken);
    }
}
