using TradeOps.Application.Models;
using TradeOps.Domain.Entities;

namespace TradeOps.Application.Interfaces;

public interface IRiskControlService
{
    Task<RiskControlSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default);

    Task<RiskControlSnapshot> SetTradingEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<RiskControlSnapshot> SetEmergencyStopAsync(
        bool enabled,
        string? reason,
        CancellationToken cancellationToken = default);

    Task RecordRejectionAsync(
        TradingSignal signal,
        RiskDecision decision,
        CancellationToken cancellationToken = default);
}
