using Microsoft.AspNetCore.Mvc;
using TradeOps.Api.Contracts;
using TradeOps.Application.Interfaces;

namespace TradeOps.Api.Controllers;

[ApiController]
[Route("api/fills")]
public sealed class FillsController(IOperatorReadRepository operatorReadRepository)
    : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<FillAuditResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyCollection<FillAuditResponse>>> GetRecent(
        [FromQuery] string? symbol = null,
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var fills = await operatorReadRepository.GetFillsAsync(
            symbol,
            limit,
            cancellationToken);

        return Ok(fills.Select(fill => new FillAuditResponse(
            fill.FillId,
            fill.OrderId,
            fill.ExchangeFillId,
            fill.ClientOrderId,
            fill.Symbol,
            fill.Side,
            fill.Quantity,
            fill.Price,
            fill.Fee,
            fill.FeeCurrency,
            fill.FilledAt)).ToArray());
    }
}
