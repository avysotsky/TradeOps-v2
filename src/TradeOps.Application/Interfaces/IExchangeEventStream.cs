using TradeOps.Application.Models;

namespace TradeOps.Application.Interfaces;

public interface IExchangeEventStream
{
    bool IsEnabled { get; }

    Task RunAsync(
        Func<ExchangeOrderUpdate, CancellationToken, Task> onOrderUpdate,
        Func<ExchangeExecutionUpdate, CancellationToken, Task> onExecutionUpdate,
        CancellationToken cancellationToken = default);
}
