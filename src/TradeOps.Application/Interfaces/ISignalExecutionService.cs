using TradeOps.Application.Models;
using TradeOps.Domain.Entities;

namespace TradeOps.Application.Interfaces;

public interface ISignalExecutionService
{
    Task<SignalExecutionResult> ExecuteSignalAsync(
        TradingSignal signal,
        CancellationToken cancellationToken = default);
}
