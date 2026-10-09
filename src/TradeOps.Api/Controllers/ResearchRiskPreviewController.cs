using Microsoft.AspNetCore.Mvc;
using TradeOps.Api.Security;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Api.Controllers;

[ApiController]
[Route("api/v1/risk")]
public sealed class ResearchRiskPreviewController(IConfiguration configuration) : ControllerBase
{
    [HttpPost("preview")]
    [OperatorApiKey]
    [RequestSizeLimit(1048576)]
    public async Task<IActionResult> Preview([FromBody] SnapshotRiskPreviewRequest request, CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>("ResearchPreview:Enabled") ||
            !configuration.GetValue<bool>("OperatorApi:Authentication:Enabled"))
            return NotFound();
        if (request.SignalId == Guid.Empty || string.IsNullOrWhiteSpace(request.Symbol) ||
            request.Symbol.Length > 50 || request.Quantity <= 0 ||
            request.Settings is null || request.Controls is null || request.Positions is null ||
            request.Positions.Count > 1000 || request.Settings.AllowedSymbols is null ||
            request.Settings.AllowedSymbols.Count > 1000 ||
            !Enum.IsDefined(request.Side))
            return BadRequest(new { error = "invalid_request" });

        var signal = new TradingSignal
        {
            Id = request.SignalId,
            Symbol = request.Symbol,
            Side = request.Side,
            RequestedQuantity = request.Quantity,
            CreatedAt = request.CreatedAt
        };
        try
        {
            var decision = await NonMutatingRiskPreview.CheckAsync(
                signal, request.Settings, request.Controls.ToSnapshot(), request.Positions, cancellationToken);
            return Ok(new SnapshotRiskPreviewResponse(1, signal.Id, decision.IsAllowed, decision.Reasons));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return UnprocessableEntity(new { error = "invalid_snapshot" });
        }
    }
}

public sealed record SnapshotRiskPreviewRequest(
    Guid SignalId, string Symbol, OrderSide Side, decimal Quantity,
    DateTimeOffset CreatedAt, RiskSettings Settings, ResearchRiskControlsRequest Controls,
    IReadOnlyCollection<Position> Positions);

public sealed record SnapshotRiskPreviewResponse(
    int SchemaVersion, Guid SignalId, bool Allowed, IReadOnlyCollection<string> Reasons);
