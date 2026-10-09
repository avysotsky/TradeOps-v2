using TradeOps.Application.Models;

namespace TradeOps.Application.Interfaces;

public interface IExchangeEventProcessor
{
    Task ProcessOrderUpdateAsync(
        ExchangeOrderUpdate update,
        CancellationToken cancellationToken = default);

    Task ProcessExecutionUpdateAsync(
        ExchangeExecutionUpdate update,
        CancellationToken cancellationToken = default);
}
