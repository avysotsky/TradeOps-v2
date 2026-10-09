using TradeOps.Application.Models;

namespace TradeOps.Application.Interfaces;

public interface IEmergencyStopService
{
    Task<EmergencyStopExecutionResult> SetAsync(
        bool enabled,
        string? reason,
        CancellationToken cancellationToken = default);
}
