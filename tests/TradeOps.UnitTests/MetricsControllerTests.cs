using Microsoft.AspNetCore.Mvc;
using TradeOps.Api.Controllers;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class MetricsControllerTests
{
    [Fact]
    public async Task Series_NormalizesWindowBucketAndSymbol()
    {
        var repository = new CapturingExecutionMetricsRepository();
        var controller = new MetricsController(repository);
        var from = DateTimeOffset.Parse("2026-10-01T12:00:00+02:00");
        var to = DateTimeOffset.Parse("2026-10-01T13:00:00+02:00");

        var action = await controller.GetExecutionSeriesAsync(
            from,
            to,
            " 5M ",
            " btcusdt ",
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var payload = Assert.IsType<ExecutionMetricsSeriesSnapshot>(ok.Value);

        Assert.Equal(DateTimeOffset.Parse("2026-10-01T10:00:00Z"), repository.SeriesFrom);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T11:00:00Z"), repository.SeriesTo);
        Assert.Equal("5m", repository.SeriesBucket);
        Assert.Equal(TimeSpan.FromMinutes(5), repository.SeriesBucketSize);
        Assert.Equal("BTCUSDT", repository.SeriesSymbol);
        Assert.Equal("5m", payload.Bucket);
        Assert.Equal("BTCUSDT", payload.Symbol);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("30m")]
    [InlineData("2h")]
    public async Task Series_RejectsUnsupportedBucket(string? bucket)
    {
        var repository = new CapturingExecutionMetricsRepository();
        var controller = new MetricsController(repository);

        var action = await controller.GetExecutionSeriesAsync(
            DateTimeOffset.Parse("2026-10-01T10:00:00Z"),
            DateTimeOffset.Parse("2026-10-01T11:00:00Z"),
            bucket,
            cancellationToken: CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(400, problem.StatusCode);
        Assert.Equal(0, repository.SeriesCallCount);
    }

    [Fact]
    public async Task Series_RejectsMoreThanFiveHundredBuckets()
    {
        var repository = new CapturingExecutionMetricsRepository();
        var controller = new MetricsController(repository);
        var from = DateTimeOffset.Parse("2026-10-01T00:00:00Z");

        var action = await controller.GetExecutionSeriesAsync(
            from,
            from.AddMinutes(501),
            "1m",
            cancellationToken: CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(400, problem.StatusCode);
        Assert.Equal(0, repository.SeriesCallCount);
    }

    [Fact]
    public async Task Series_AllowsExactlyFiveHundredBuckets()
    {
        var repository = new CapturingExecutionMetricsRepository();
        var controller = new MetricsController(repository);
        var from = DateTimeOffset.Parse("2026-10-01T00:00:00Z");

        var action = await controller.GetExecutionSeriesAsync(
            from,
            from.AddMinutes(500),
            "1m",
            cancellationToken: CancellationToken.None);

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.Equal(1, repository.SeriesCallCount);
    }

    private sealed class CapturingExecutionMetricsRepository : IExecutionMetricsRepository
    {
        public int SeriesCallCount { get; private set; }
        public DateTimeOffset? SeriesFrom { get; private set; }
        public DateTimeOffset? SeriesTo { get; private set; }
        public string? SeriesBucket { get; private set; }
        public TimeSpan? SeriesBucketSize { get; private set; }
        public string? SeriesSymbol { get; private set; }

        public Task<ExecutionMetricsSnapshot> GetAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ExecutionMetricsWindowSnapshot> GetWindowAsync(
            DateTimeOffset fromInclusive,
            DateTimeOffset toExclusive,
            string? symbol = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ExecutionMetricsSeriesSnapshot> GetSeriesAsync(
            DateTimeOffset fromInclusive,
            DateTimeOffset toExclusive,
            string bucket,
            TimeSpan bucketSize,
            string? symbol = null,
            CancellationToken cancellationToken = default)
        {
            SeriesCallCount++;
            SeriesFrom = fromInclusive;
            SeriesTo = toExclusive;
            SeriesBucket = bucket;
            SeriesBucketSize = bucketSize;
            SeriesSymbol = symbol;

            return Task.FromResult(new ExecutionMetricsSeriesSnapshot(
                DateTimeOffset.UtcNow,
                fromInclusive,
                toExclusive,
                bucket,
                symbol,
                Array.Empty<ExecutionMetricsSeriesBucket>()));
        }
    }
}
