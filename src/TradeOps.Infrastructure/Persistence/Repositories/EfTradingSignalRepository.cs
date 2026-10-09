using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Application.Interfaces;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Persistence.Repositories;

public sealed class EfTradingSignalRepository(TradeOpsDbContext dbContext)
    : ITradingSignalRepository
{
    public Task<TradingSignal?> GetByIdAsync(
        Guid signalId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.TradingSignals
            .AsNoTracking()
            .FirstOrDefaultAsync(
                signal => signal.Id == signalId,
                cancellationToken);
    }

    public async Task<bool> TryAddAsync(
        TradingSignal signal,
        CancellationToken cancellationToken = default)
    {
        var receivedEvent = CreateOutcomeEvent(signal, previousOutcome: null);

        dbContext.TradingSignals.Add(signal);
        dbContext.TradingSignalOutcomeEvents.Add(receivedEvent);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            dbContext.Entry(signal).State = EntityState.Detached;
            dbContext.Entry(receivedEvent).State = EntityState.Detached;
            return false;
        }
    }

    public async Task UpdateAsync(
        TradingSignal signal,
        CancellationToken cancellationToken = default)
    {
        var currentOutcome = await dbContext.TradingSignals
            .AsNoTracking()
            .Where(item => item.Id == signal.Id)
            .Select(item => (SignalOutcome?)item.Outcome)
            .SingleOrDefaultAsync(cancellationToken);

        if (currentOutcome is null)
        {
            throw new InvalidOperationException(
                $"Trading signal '{signal.Id}' cannot be updated because it is not persisted.");
        }

        dbContext.TradingSignals.Update(signal);

        if (currentOutcome.Value != signal.Outcome)
        {
            dbContext.TradingSignalOutcomeEvents.Add(
                CreateOutcomeEvent(signal, currentOutcome.Value));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static TradingSignalOutcomeEvent CreateOutcomeEvent(
        TradingSignal signal,
        SignalOutcome? previousOutcome)
    {
        return new TradingSignalOutcomeEvent
        {
            Id = Guid.NewGuid(),
            TradingSignalId = signal.Id,
            PreviousOutcome = previousOutcome,
            Outcome = signal.Outcome,
            OccurredAt = DateTimeOffset.UtcNow,
            RiskRejectionReasons = signal.RiskRejectionReasons.ToArray(),
            OrderId = signal.OrderId,
            ClientOrderId = signal.ClientOrderId
        };
    }
}
