using Microsoft.AspNetCore.Mvc;
using TradeOps.Api.Security;
using TradeOps.Application.Models;
using TradeOps.Application.Services;

namespace TradeOps.Api.Controllers;

/// <summary>Query-only research and rebalance previews. Never submits orders.</summary>
[ApiController]
[Route("api/v1")]
public sealed class ResearchPreviewController(IConfiguration configuration) : ControllerBase
{
    private bool Disabled =>
        !configuration.GetValue<bool>("ResearchPreview:Enabled") ||
        !configuration.GetValue<bool>(OperatorApiAuthOptions.SectionName + ":Enabled");

    [HttpPost("research/decisions")]
    [OperatorApiKey]
    [RequestSizeLimit(1_048_576)]
    public IActionResult Decision([FromBody] ResearchDecision? decision)
    {
        if (Disabled)
            return NotFound();
        var validation = ResearchDecisionValidator.Validate(decision);
        return validation.IsValid
            ? Ok(validation)
            : BadRequest(new { error = "invalid_research_decision" });
    }

    [HttpPost("research/decisions/derive")]
    [OperatorApiKey]
    [RequestSizeLimit(1_048_576)]
    public IActionResult Derive([FromBody] TranscriptResearchDecisionDemoRequest? request)
    {
        if (Disabled)
            return NotFound();
        if (request?.Prior is null || request.Current is null || request.Policy is null ||
            request.Prior.NormalizedDocumentJson?.Length > 500_000 ||
            request.Current.NormalizedDocumentJson?.Length > 500_000 ||
            request.Prior.StructuredExtractionJson?.Length > 500_000 ||
            request.Current.StructuredExtractionJson?.Length > 500_000)
            return BadRequest(new { error = "invalid_request" });

        try
        {
            var result = new TranscriptResearchDecisionDemoService().Run(request);
            return Ok(new {
                schemaVersion = 1,
                result.Decision,
                result.Assessment,
                result.PolicyFingerprint
            });
        }
        catch (Exception)
        {
            return BadRequest(new { error = "invalid_research_artifacts" });
        }
    }

    [HttpPost("rebalance/preview")]
    [OperatorApiKey]
    [RequestSizeLimit(1_048_576)]
    public IActionResult Rebalance([FromBody] RebalancePreviewHttpRequest? request)
    {
        if (Disabled)
            return NotFound();
        if (request?.Decision is null || request.Portfolio is null ||
            request.Portfolio.Positions is null || request.Portfolio.Positions.Count > 1000)
            return BadRequest(new { error = "invalid_request" });

        try
        {
            var plan = PortfolioRebalancePlanner.Plan(
                request.Decision, request.Portfolio, request.ReferencePrice,
                request.Constraints);
            return Ok(plan);
        }
        catch (Exception)
        {
            return BadRequest(new { error = "invalid_rebalance_input" });
        }
    }
}

public sealed record RebalancePreviewHttpRequest(
    ResearchDecision Decision,
    PortfolioSnapshot Portfolio,
    decimal? ReferencePrice,
    RebalanceConstraints? Constraints);
