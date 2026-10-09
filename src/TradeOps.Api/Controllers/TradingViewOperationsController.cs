using Microsoft.AspNetCore.Mvc;
using TradeOps.Api.Contracts;
using TradeOps.Api.Security;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;

namespace TradeOps.Api.Controllers;

[ApiController]
[Route("api/integrations/tradingview/operations")]
public sealed class TradingViewOperationsController(
    ITradingViewDeliveryAuditRepository auditRepository,
    ITradingViewDeliveryHealthStateRepository healthStateRepository) : ControllerBase
{
    [HttpGet("deliveries")]
    [OperatorApiKey]
    [ProducesResponseType<IReadOnlyCollection<TradingViewDeliveryAuditResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyCollection<TradingViewDeliveryAuditResponse>>> GetDeliveries(
        [FromQuery] string? eventId = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 200)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Delivery audit limit must be between 1 and 200.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var deliveries = await auditRepository.GetAsync(
            eventId,
            limit,
            cancellationToken);

        return Ok(deliveries.Select(ToResponse).ToArray());
    }

    [HttpGet("metrics")]
    [OperatorApiKey]
    [ProducesResponseType<TradingViewDeliveryMetricsSnapshot>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TradingViewDeliveryMetricsSnapshot>> GetMetrics(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        CancellationToken cancellationToken = default)
    {
        var toExclusive = to ?? DateTimeOffset.UtcNow;
        var fromInclusive = from ?? toExclusive.AddHours(-24);

        if (fromInclusive >= toExclusive)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "'from' must be earlier than 'to'.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (toExclusive - fromInclusive > TimeSpan.FromDays(31))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "TradingView delivery metrics window must not exceed 31 days.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        return Ok(await auditRepository.GetMetricsAsync(
            fromInclusive,
            toExclusive,
            cancellationToken));
    }

    [HttpGet("health")]
    [OperatorApiKey]
    [ProducesResponseType<TradingViewDeliveryHealthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TradingViewDeliveryHealthResponse>> GetHealth(
        CancellationToken cancellationToken = default)
    {
        var state = await healthStateRepository.GetAsync(
            cancellationToken);

        if (state is null)
        {
            return NotFound();
        }

        return Ok(new TradingViewDeliveryHealthResponse(
            state.Status,
            state.Reason,
            state.UpdatedAt,
            state.WindowFrom,
            state.WindowTo,
            state.Total,
            state.Failed,
            state.Conflict,
            state.RiskRejected,
            state.AverageLatencyMilliseconds,
            state.LatestDeliveryAt,
            state.LatestSuccessfulAt));
    }

    private static TradingViewDeliveryAuditResponse ToResponse(
        TradingViewDeliveryAudit item)
    {
        return new TradingViewDeliveryAuditResponse(
            item.Id,
            item.EventId,
            item.ReceivedAt,
            item.CompletedAt,
            item.DurationMilliseconds,
            item.Outcome,
            item.HttpStatusCode,
            item.SignalId,
            item.OrderId,
            item.ClientOrderId,
            item.Symbol,
            item.Action);
    }
}
