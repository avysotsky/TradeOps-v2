using Microsoft.AspNetCore.Mvc;
using TradeOps.Application.Interfaces;
using TradeOps.Domain.Entities;

namespace TradeOps.Api.Controllers;

[ApiController]
[Route("api/account")]
public sealed class AccountController(IExchangeClient exchangeClient) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<AccountInfo>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AccountInfo>> Get(
        CancellationToken cancellationToken)
    {
        var account = await exchangeClient.GetAccountAsync(cancellationToken);
        return Ok(account);
    }
}
