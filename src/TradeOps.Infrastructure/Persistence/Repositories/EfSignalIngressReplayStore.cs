using Microsoft.EntityFrameworkCore;
using Npgsql;
using TradeOps.Application.Interfaces;

namespace TradeOps.Infrastructure.Persistence.Repositories;

public sealed class EfSignalIngressReplayStore(
    TradeOpsDbContext dbContext) : ISignalIngressReplayStore
{
    public async Task<bool> TryRegisterAsync(
        Guid requestId,
        DateTimeOffset requestTimestamp,
        DateTimeOffset receivedAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await dbContext.SignalIngressReplayReceipts
            .Where(receipt => receipt.ExpiresAt < receivedAt)
            .ExecuteDeleteAsync(cancellationToken);

        var receipt = new SignalIngressReplayReceipt
        {
            RequestId = requestId,
            RequestTimestamp = requestTimestamp,
            ReceivedAt = receivedAt,
            ExpiresAt = expiresAt
        };

        dbContext.SignalIngressReplayReceipts.Add(receipt);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation
            })
        {
            dbContext.Entry(receipt).State = EntityState.Detached;
            return false;
        }
    }
}
