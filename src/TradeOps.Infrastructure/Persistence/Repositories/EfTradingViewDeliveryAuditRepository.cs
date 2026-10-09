using Microsoft.EntityFrameworkCore;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Persistence.Repositories;

public sealed class EfTradingViewDeliveryAuditRepository(
    TradeOpsDbContext dbContext) : ITradingViewDeliveryAuditRepository
{
    public async Task<Guid> StartAsync(
        string? eventId,
        string? symbol,
        string? action,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken = default)
    {
        var audit = new TradingViewDeliveryAudit
        {
            Id = Guid.NewGuid(),
            EventId = Normalize(eventId),
            ReceivedAt = receivedAt,
            Outcome = TradingViewDeliveryOutcome.Received,
            Symbol = Normalize(symbol)?.ToUpperInvariant(),
            Action = Normalize(action)?.ToLowerInvariant()
        };

        dbContext.TradingViewDeliveryAudits.Add(audit);
        await dbContext.SaveChangesAsync(cancellationToken);

        return audit.Id;
    }

    public async Task CompleteAsync(
        Guid deliveryId,
        TradingViewDeliveryOutcome outcome,
        int httpStatusCode,
        Guid? signalId = null,
        Guid? orderId = null,
        string? clientOrderId = null,
        CancellationToken cancellationToken = default)
    {
        var audit = await dbContext.TradingViewDeliveryAudits
            .SingleOrDefaultAsync(
                item => item.Id == deliveryId,
                cancellationToken);

        if (audit is null)
        {
            throw new InvalidOperationException(
                $"TradingView delivery audit '{deliveryId}' was not found.");
        }

        var completedAt = DateTimeOffset.UtcNow;

        audit.Outcome = outcome;
        audit.HttpStatusCode = httpStatusCode;
        audit.SignalId = signalId;
        audit.OrderId = orderId;
        audit.ClientOrderId = clientOrderId;
        audit.CompletedAt = completedAt;
        audit.DurationMilliseconds = Math.Max(
            0L,
            (long)Math.Round(
                (completedAt - audit.ReceivedAt).TotalMilliseconds,
                MidpointRounding.AwayFromZero));

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<TradingViewDeliveryAudit>> GetAsync(
        string? eventId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var normalizedEventId = Normalize(eventId);

        var query = dbContext.TradingViewDeliveryAudits
            .AsNoTracking()
            .AsQueryable();

        if (normalizedEventId is not null)
        {
            query = query.Where(
                item => item.EventId == normalizedEventId);
        }

        return await query
            .OrderByDescending(item => item.ReceivedAt)
            .ThenByDescending(item => item.Id)
            .Take(limit)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<TradingViewDeliveryMetricsSnapshot> GetMetricsAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.TradingViewDeliveryAudits
            .AsNoTracking()
            .Where(item =>
                item.ReceivedAt >= fromInclusive
                && item.ReceivedAt < toExclusive);

        var total = await query.CountAsync(cancellationToken);
        var pending = await query.CountAsync(
            item => item.Outcome == TradingViewDeliveryOutcome.Received,
            cancellationToken);
        var accepted = await query.CountAsync(
            item => item.Outcome == TradingViewDeliveryOutcome.Accepted,
            cancellationToken);
        var redelivered = await query.CountAsync(
            item => item.Outcome == TradingViewDeliveryOutcome.Redelivered,
            cancellationToken);
        var validationRejected = await query.CountAsync(
            item => item.Outcome == TradingViewDeliveryOutcome.ValidationRejected,
            cancellationToken);
        var riskRejected = await query.CountAsync(
            item => item.Outcome == TradingViewDeliveryOutcome.RiskRejected,
            cancellationToken);
        var conflict = await query.CountAsync(
            item => item.Outcome == TradingViewDeliveryOutcome.Conflict,
            cancellationToken);
        var failed = await query.CountAsync(
            item => item.Outcome == TradingViewDeliveryOutcome.Failed,
            cancellationToken);

        var completed = query.Where(
            item => item.DurationMilliseconds.HasValue);

        double? averageLatency = null;
        long? maxLatency = null;

        if (await completed.AnyAsync(cancellationToken))
        {
            averageLatency = await completed.AverageAsync(
                item => (double?)item.DurationMilliseconds,
                cancellationToken);
            maxLatency = await completed.MaxAsync(
                item => item.DurationMilliseconds,
                cancellationToken);
        }

        var latestDeliveryAt = await dbContext
            .TradingViewDeliveryAudits
            .AsNoTracking()
            .MaxAsync(
                item => (DateTimeOffset?)item.ReceivedAt,
                cancellationToken);

        var latestSuccessfulAt = await dbContext
            .TradingViewDeliveryAudits
            .AsNoTracking()
            .Where(item =>
                item.Outcome == TradingViewDeliveryOutcome.Accepted
                || item.Outcome == TradingViewDeliveryOutcome.Redelivered)
            .MaxAsync(
                item => (DateTimeOffset?)item.ReceivedAt,
                cancellationToken);

        return new TradingViewDeliveryMetricsSnapshot(
            DateTimeOffset.UtcNow,
            fromInclusive,
            toExclusive,
            total,
            pending,
            accepted,
            redelivered,
            validationRejected,
            riskRejected,
            conflict,
            failed,
            averageLatency,
            maxLatency,
            latestDeliveryAt,
            latestSuccessfulAt);
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }
}
