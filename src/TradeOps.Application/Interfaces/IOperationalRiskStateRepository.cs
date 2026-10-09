using TradeOps.Domain.Entities;

namespace TradeOps.Application.Interfaces;

public interface IOperationalRiskStateRepository
{
    Task<OperationalRiskState> GetOrCreateAsync(
        bool initialTradingEnabled,
        bool initialEmergencyStop,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        OperationalRiskState state,
        CancellationToken cancellationToken = default);
}
