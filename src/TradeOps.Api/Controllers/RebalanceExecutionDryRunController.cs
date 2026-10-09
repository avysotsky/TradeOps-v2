using Microsoft.AspNetCore.Mvc;
using TradeOps.Api.Security;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Entities;

namespace TradeOps.Api.Controllers;

/// <summary>Opt-in offline preview. Not an executable order or execution authorization.</summary>
[ApiController]
[Route("api/v1/execution")]
public sealed class RebalanceExecutionDryRunController(IConfiguration configuration) : ControllerBase
{
    [HttpPost("dry-run")]
    [OperatorApiKey]
    [RequestSizeLimit(1_048_576)]
    public async Task<IActionResult> DryRun(
        [FromBody] ExecutionDryRunHttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>("ResearchPreview:Enabled") ||
            !configuration.GetValue<bool>(OperatorApiAuthOptions.SectionName + ":Enabled"))
            return NotFound();

        if (request?.Plan is null || request.Settings is null ||
            request.Controls is null || request.Positions is null ||
            request.Positions.Count > 1000 || request.Settings.AllowedSymbols is null ||
            request.Settings.AllowedSymbols.Count > 1000)
            return BadRequest(new { error = "invalid_request" });

        try
        {
            var result = await RebalanceExecutionDryRunPreview.RunAsync(
                request.Plan, request.Settings, request.Controls.ToSnapshot(), request.Positions,
                cancellationToken);
            return Ok(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException)
        {
            return BadRequest(new { error = "invalid_intent" });
        }
        catch (Exception)
        {
            return UnprocessableEntity(new { error = "invalid_preview_snapshot" });
        }
    }
}

public sealed record ExecutionDryRunHttpRequest(
    RebalancePlan Plan, RiskSettings Settings, ResearchRiskControlsRequest Controls,
    IReadOnlyCollection<Position> Positions);
