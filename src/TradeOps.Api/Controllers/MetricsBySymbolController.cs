using Microsoft.AspNetCore.Mvc;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;

namespace TradeOps.Api.Controllers;

[ApiController]
[Route("api/metrics")]
public sealed class MetricsBySymbolController(
    IExecutionMetricsBySymbolRepository metricsRepository) : ControllerBase
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;

    [HttpGet("execution/by-symbol")]
    [ProducesResponseType(typeof(ExecutionMetricsBySymbolSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ExecutionMetricsBySymbolSnapshot>> GetExecutionBySymbolAsync(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (from is null || to is null)
        {
            return InvalidWindow("Both 'from' and 'to' query parameters are required.");
        }

        var fromUtc = from.Value.ToUniversalTime();
        var toUtc = to.Value.ToUniversalTime();
        if (fromUtc >= toUtc)
        {
            return InvalidWindow("'from' must be earlier than 'to'. The window uses [from, to) semantics.");
        }

        var resolvedLimit = limit ?? DefaultLimit;
        if (resolvedLimit is < 1 or > MaxLimit)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid execution metrics symbol limit",
                detail: $"'limit' must be between 1 and {MaxLimit}. The default is {DefaultLimit}.");
        }

        var metrics = await metricsRepository.GetAsync(
            fromUtc,
            toUtc,
            resolvedLimit,
            cancellationToken);

        return Ok(metrics);
    }

    private ActionResult InvalidWindow(string detail) =>
        Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid execution metrics window",
            detail: detail);
}
