using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;

namespace TradeOps.Application.Services;

public sealed class EmergencyStopService(
    IRiskControlService riskControlService,
    IOrderBulkCancellationService orderBulkCancellationService,
    IAlertService alertService,
    ILogger<EmergencyStopService> logger) : IEmergencyStopService
{
    public async Task<EmergencyStopExecutionResult> SetAsync(
        bool enabled,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var risk = await riskControlService.SetEmergencyStopAsync(
            enabled,
            reason,
            cancellationToken);

        if (!enabled)
        {
            return new EmergencyStopExecutionResult(risk, null);
        }

        var cancellation = await orderBulkCancellationService.CancelOpenOrdersAsync(
            cancellationToken: cancellationToken);

        if (!cancellation.IsComplete)
        {
            logger.LogWarning(
                "Emergency stop is active, but order cancellation is incomplete: pending={PendingCount}, unresolved={UnresolvedCount}, notFound={NotFoundCount}.",
                cancellation.CancellationRequestedCount,
                cancellation.UnresolvedCount,
                cancellation.NotFoundCount);

            await alertService.SendAsync(
                new AlertMessage(
                    "EmergencyStopCancellationIncomplete",
                    $"Emergency stop is active, but cancellation is incomplete: pending={cancellation.CancellationRequestedCount}, unresolved={cancellation.UnresolvedCount}, notFound={cancellation.NotFoundCount}.",
                    AlertSeverity.Critical),
                cancellationToken);
        }

        return new EmergencyStopExecutionResult(risk, cancellation);
    }
}
