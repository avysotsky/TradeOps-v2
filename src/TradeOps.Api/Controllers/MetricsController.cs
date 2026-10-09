using Microsoft.AspNetCore.Mvc;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;

namespace TradeOps.Api.Controllers;

[ApiController]
[Route("api/metrics")]
public sealed class MetricsController(IExecutionMetricsRepository metricsRepository) : ControllerBase
{
    private const int MaxSeriesBuckets = 500;

    private static readonly IReadOnlyDictionary<string, TimeSpan> SupportedBuckets =
        new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase)
        {
            ["1m"] = TimeSpan.FromMinutes(1),
            ["5m"] = TimeSpan.FromMinutes(5),
            ["15m"] = TimeSpan.FromMinutes(15),
            ["1h"] = TimeSpan.FromHours(1),
            ["1d"] = TimeSpan.FromDays(1)
        };

    [HttpGet("execution")]
    [ProducesResponseType(typeof(ExecutionMetricsSnapshot), StatusCodes.Status200OK)]
    public async Task<ActionResult<ExecutionMetricsSnapshot>> GetExecutionAsync(
        CancellationToken cancellationToken)
    {
        var metrics = await metricsRepository.GetAsync(cancellationToken);
        return Ok(metrics);
    }

    [HttpGet("execution/window")]
    [ProducesResponseType(typeof(ExecutionMetricsWindowSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ExecutionMetricsWindowSnapshot>> GetExecutionWindowAsync(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateWindowAndSymbol(from, to, symbol);
        if (validation.Problem is not null)
        {
            return validation.Problem;
        }

        var metrics = await metricsRepository.GetWindowAsync(
            validation.FromUtc!.Value,
            validation.ToUtc!.Value,
            validation.Symbol,
            cancellationToken);

        return Ok(metrics);
    }

    [HttpGet("execution/series")]
    [ProducesResponseType(typeof(ExecutionMetricsSeriesSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ExecutionMetricsSeriesSnapshot>> GetExecutionSeriesAsync(
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery] string? bucket = null,
        [FromQuery] string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateWindowAndSymbol(from, to, symbol);
        if (validation.Problem is not null)
        {
            return validation.Problem;
        }

        var normalizedBucket = string.IsNullOrWhiteSpace(bucket)
            ? null
            : bucket.Trim().ToLowerInvariant();

        if (normalizedBucket is null || !SupportedBuckets.TryGetValue(normalizedBucket, out var bucketSize))
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid execution metrics bucket",
                detail: "'bucket' is required and must be one of: 1m, 5m, 15m, 1h, 1d.");
        }

        var firstBucketStart = AlignToBucketStart(validation.FromUtc!.Value, bucketSize);
        var bucketCount = CalculateBucketCount(firstBucketStart, validation.ToUtc!.Value, bucketSize);
        if (bucketCount > MaxSeriesBuckets)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Execution metrics series is too large",
                detail: $"The requested series contains {bucketCount} buckets; the maximum is {MaxSeriesBuckets}.");
        }

        var metrics = await metricsRepository.GetSeriesAsync(
            validation.FromUtc.Value,
            validation.ToUtc.Value,
            normalizedBucket,
            bucketSize,
            validation.Symbol,
            cancellationToken);

        return Ok(metrics);
    }

    private ActionResult InvalidWindow(string detail) =>
        Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid execution metrics window",
            detail: detail);

    private (DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, string? Symbol, ActionResult? Problem)
        ValidateWindowAndSymbol(
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? symbol)
    {
        if (from is null || to is null)
        {
            return (null, null, null, InvalidWindow("Both 'from' and 'to' query parameters are required."));
        }

        var fromUtc = from.Value.ToUniversalTime();
        var toUtc = to.Value.ToUniversalTime();

        if (fromUtc >= toUtc)
        {
            return (null, null, null, InvalidWindow("'from' must be earlier than 'to'. The window uses [from, to) semantics."));
        }

        var normalizedSymbol = string.IsNullOrWhiteSpace(symbol)
            ? null
            : symbol.Trim().ToUpperInvariant();

        if (normalizedSymbol is { Length: > 50 })
        {
            return (
                null,
                null,
                null,
                Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid execution metrics symbol",
                    detail: "'symbol' must be 50 characters or fewer after trimming."));
        }

        return (fromUtc, toUtc, normalizedSymbol, null);
    }

    private static DateTimeOffset AlignToBucketStart(DateTimeOffset timestamp, TimeSpan bucketSize)
    {
        var utcTicksSinceEpoch = timestamp.ToUniversalTime().UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks;
        var quotient = Math.DivRem(utcTicksSinceEpoch, bucketSize.Ticks, out var remainder);
        if (remainder < 0)
        {
            quotient--;
        }

        return DateTimeOffset.UnixEpoch.AddTicks(quotient * bucketSize.Ticks);
    }

    private static long CalculateBucketCount(
        DateTimeOffset firstBucketStart,
        DateTimeOffset toExclusive,
        TimeSpan bucketSize)
    {
        var spanTicks = toExclusive.UtcDateTime.Ticks - firstBucketStart.UtcDateTime.Ticks;
        return (spanTicks + bucketSize.Ticks - 1) / bucketSize.Ticks;
    }
}
