using Microsoft.AspNetCore.Mvc;
using TradeOps.Application.Models;
using TradeOps.Application.Services;

namespace TradeOps.Api.Controllers;

[ApiController]
[Route("api/research-decisions")]
public sealed class ResearchDecisionsController : ControllerBase
{
    [HttpPost("validate")]
    [ProducesResponseType<ResearchDecisionValidationResult>(
        StatusCodes.Status200OK)]
    [ProducesResponseType<ResearchDecisionValidationResult>(
        StatusCodes.Status400BadRequest)]
    public ActionResult<ResearchDecisionValidationResult> Validate(
        ResearchDecision? decision)
    {
        var result =
            ResearchDecisionValidator.Validate(
                decision);

        return result.IsValid
            ? Ok(result)
            : BadRequest(result);
    }
}
