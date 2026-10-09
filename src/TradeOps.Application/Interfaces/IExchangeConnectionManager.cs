namespace TradeOps.Application.Interfaces;

public interface IExchangeConnectionManager
{
    bool IsConnected { get; }

    Task EnsureConnectedAsync(
        CancellationToken cancellationToken = default);
}
