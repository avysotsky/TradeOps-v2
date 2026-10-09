using Microsoft.AspNetCore.Mvc;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;

namespace TradeOps.Api.Controllers;

[ApiController]
[Route("api/exchange/capabilities")]
public sealed class ExchangeCapabilitiesController(
    IExchangeCapabilityCatalog capabilityCatalog) : ControllerBase
{
    [HttpGet("current")]
    [ProducesResponseType<ExchangeCapabilityProfile>(StatusCodes.Status200OK)]
    public ActionResult<ExchangeCapabilityProfile> GetCurrent() =>
        Ok(capabilityCatalog.Current);

    [HttpGet]
    [ProducesResponseType<IReadOnlyCollection<ExchangeCapabilityProfile>>(
        StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyCollection<ExchangeCapabilityProfile>> GetAll() =>
        Ok(capabilityCatalog.All);
}
