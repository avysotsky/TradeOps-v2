using Microsoft.AspNetCore.Mvc;
using TradeOps.Api.Contracts;
using TradeOps.Application.Interfaces;

namespace TradeOps.Api.Controllers;

[ApiController]
[Route("api/pnl")]
public sealed class PnlController(IRiskControlService riskControlService) : ControllerBase
{
    [HttpGet("daily")]
    [ProducesResponseType<DailyPnlResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DailyPnlResponse>> GetDaily(
        CancellationToken cancellationToken)
    {
        var snapshot = await riskControlService.GetSnapshotAsync(cancellationToken);

        return Ok(new DailyPnlResponse(
            DateOnly.FromDateTime(DateTime.UtcNow),
            snapshot.SettlementCurrency,
            snapshot.DailyGrossRealizedPnL,
            snapshot.DailySettlementFees,
            snapshot.DailyNetRealizedPnL,
            snapshot.UnconvertedFees
                .Select(fee => new UnconvertedFeeResponse(
                    fee.ExchangeFillId,
                    fee.Amount,
                    fee.Currency,
                    fee.FilledAt))
                .ToArray(),
            snapshot.IsDailyAccountingComplete));
    }
}
