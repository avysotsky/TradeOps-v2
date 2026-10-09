using Microsoft.AspNetCore.Mvc;
using TradeOps.Api.Contracts;
using TradeOps.Api.Security;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;

namespace TradeOps.Api.Controllers;

[ApiController]
[Route("api/risk")]
public sealed class RiskController(
    RiskSettings settings,
    IRiskControlService riskControlService,
    IEmergencyStopService emergencyStopService,
    IRiskEventRepository riskEventRepository) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(
        CancellationToken cancellationToken)
    {
        var control = await riskControlService.GetSnapshotAsync(cancellationToken);

        return Ok(new
        {
            settings.MaxPositionSize,
            settings.MaxOrderSize,
            settings.MaxDailyLoss,
            settings.MaxOpenPositions,
            AllowedSymbols = settings.AllowedSymbols.OrderBy(x => x),
            control.TradingEnabled,
            control.EmergencyStop,
            control.EmergencyStopReason,
            control.SettlementCurrency,
            control.DailyGrossRealizedPnL,
            control.DailySettlementFees,
            control.DailyNetRealizedPnL,
            control.UnconvertedFees,
            control.IsDailyAccountingComplete,
            control.ActivePositionMismatchCount,
            control.HasPositionMismatch,
            control.UpdatedAt
        });
    }

    [HttpPost("trading-enabled")]
    [OperatorApiKey]
    [ProducesResponseType<RiskControlSnapshot>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<RiskControlSnapshot>> SetTradingEnabled(
        [FromBody] SetTradingEnabledRequest request,
        CancellationToken cancellationToken)
    {
        var result = await riskControlService.SetTradingEnabledAsync(
            request.Enabled,
            cancellationToken);
        return Ok(result);
    }

    [HttpPost("emergency-stop")]
    [OperatorApiKey]
    [ProducesResponseType<EmergencyStopResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<EmergencyStopResponse>> SetEmergencyStop(
        [FromBody] SetEmergencyStopRequest request,
        CancellationToken cancellationToken)
    {
        var result = await emergencyStopService.SetAsync(
            request.Enabled,
            request.Reason,
            cancellationToken);
        var risk = result.Risk;

        return Ok(new EmergencyStopResponse(
            risk.TradingEnabled,
            risk.EmergencyStop,
            risk.EmergencyStopReason,
            risk.SettlementCurrency,
            risk.DailyGrossRealizedPnL,
            risk.DailySettlementFees,
            risk.DailyNetRealizedPnL,
            risk.UnconvertedFees,
            risk.IsDailyAccountingComplete,
            risk.ActivePositionMismatchCount,
            risk.HasPositionMismatch,
            risk.UpdatedAt,
            result.OrderCancellation is null
                ? null
                : OperatorResponseMapper.ToBulkOrderCancellation(result.OrderCancellation)));
    }

    [HttpGet("events")]
    [ProducesResponseType<IReadOnlyCollection<RiskEvent>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<RiskEvent>>> GetEvents(
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var events = await riskEventRepository.GetRecentAsync(limit, cancellationToken);
        return Ok(events);
    }
}

public sealed record SetTradingEnabledRequest(bool Enabled);

public sealed record SetEmergencyStopRequest(bool Enabled, string? Reason = null);
