using TradeOps.Application.Models;

namespace TradeOps.Application.Interfaces;

public interface ITradingViewDeliveryHealthEvaluator
{
    Task<TradingViewDeliveryHealthEvaluation> EvaluateAsync(
        CancellationToken cancellationToken = default);
}
