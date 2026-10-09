using TradeOps.Application.Models;
using TradeOps.Domain.Entities;

namespace TradeOps.Application.Interfaces;

public interface IRiskEngine
{
    Task<RiskDecision> CheckAsync(
        TradingSignal signal,
        CancellationToken cancellationToken = default);
}
