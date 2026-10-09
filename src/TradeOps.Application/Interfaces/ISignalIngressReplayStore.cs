namespace TradeOps.Application.Interfaces;

public interface ISignalIngressReplayStore
{
    Task<bool> TryRegisterAsync(
        Guid requestId,
        DateTimeOffset requestTimestamp,
        DateTimeOffset receivedAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);
}
