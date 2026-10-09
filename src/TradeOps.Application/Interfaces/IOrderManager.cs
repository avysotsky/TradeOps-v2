using TradeOps.Application.Models;
using TradeOps.Domain.Entities;

namespace TradeOps.Application.Interfaces;

public interface IOrderManager
{
    Task<SignalExecutionResult> ExecuteSignalAsync(
        TradingSignal signal,
        CancellationToken cancellationToken = default);
}
