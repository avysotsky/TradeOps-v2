using Microsoft.AspNetCore.Mvc;
using TradeOps.Api.Contracts;
using TradeOps.Api.Security;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;

namespace TradeOps.Api.Controllers;

[ApiController]
[Route("api/system")]
public sealed class SystemController(
    IOrderReconciliationService reconciliationService,
    IPositionReconciliationService positionReconciliationService,
    IOperationalRunStatusRepository runStatusRepository,
    IOperationalRunHistoryRepository runHistoryRepository) : ControllerBase
{
    [HttpPost("reconcile")]
    [OperatorApiKey]
    [ProducesResponseType<ReconciliationSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ReconciliationSummary>> Reconcile(
        CancellationToken cancellationToken)
    {
        var result = await reconciliationService.ReconcileAsync(cancellationToken);
        return Ok(result);
    }

    [HttpPost("reconcile/positions")]
    [OperatorApiKey]
    [ProducesResponseType<PositionReconciliationSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PositionReconciliationSummary>> ReconcilePositions(
        CancellationToken cancellationToken)
    {
        var result = await positionReconciliationService.ReconcileAsync(cancellationToken);
        return Ok(result);
    }

    [HttpGet("reconciliation/status")]
    [OperatorApiKey]
    [ProducesResponseType<OperationalRunStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OperationalRunStatusResponse>> GetReconciliationStatus(
        CancellationToken cancellationToken)
    {
        var status = await runStatusRepository.GetAsync(
            OperationalRunTypes.OrderReconciliation,
            cancellationToken);

        return status is null ? NotFound() : Ok(ToResponse(status));
    }

    [HttpGet("recovery/status")]
    [ProducesResponseType<OperationalRunStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OperationalRunStatusResponse>> GetRecoveryStatus(
        CancellationToken cancellationToken)
    {
        var status = await runStatusRepository.GetAsync(
            OperationalRunTypes.RecoveryCycle,
            cancellationToken);

        return status is null ? NotFound() : Ok(ToResponse(status));
    }

    [HttpGet("runs")]
    [ProducesResponseType<IReadOnlyCollection<OperationalRunResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<OperationalRunResponse>>> GetRuns(
        [FromQuery] string? runType = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var runs = await runHistoryRepository.GetRecentAsync(
            runType,
            limit,
            cancellationToken);

        return Ok(runs.Select(ToResponse).ToArray());
    }

    private static OperationalRunStatusResponse ToResponse(OperationalRunStatus status) => new(
        status.RunType,
        status.StartedAt,
        status.CompletedAt,
        status.IsRunning,
        status.Succeeded,
        status.OrdersScanned,
        status.OrdersUpdated,
        status.OrderIssues,
        status.OrdersMissingOnExchange,
        status.PositionsCompared,
        status.PositionMismatches,
        status.PositionSnapshots,
        status.ErrorMessage,
        status.UpdatedAt);

    private static OperationalRunResponse ToResponse(OperationalRunRecord run) => new(
        run.Id,
        run.RunType,
        run.StartedAt,
        run.CompletedAt,
        run.IsRunning,
        run.Succeeded,
        run.OrdersScanned,
        run.OrdersUpdated,
        run.OrderIssues,
        run.OrdersMissingOnExchange,
        run.PositionsCompared,
        run.PositionMismatches,
        run.PositionSnapshots,
        run.ErrorMessage,
        run.UpdatedAt);
}
