using Microsoft.AspNetCore.Mvc;
using TradeOps.Api.Controllers;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class SignalTransitionMetricsControllerTests
{
    [Fact]
    public async Task Window_NormalizesUtcWindowAndSymbol()
    {
        var repository = new CapturingRepository();
        var controller = new SignalTransitionMetricsController(repository);

        var action = await controller.GetWindowAsync(
            DateTimeOffset.Parse("2026-10-01T12:00:00+02:00"),
            DateTimeOffset.Parse("2026-10-01T13:00:00+02:00"),
            " btcusdt ",
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var payload = Assert.IsType<SignalTransitionMetricsWindowSnapshot>(ok.Value);

        Assert.Equal(DateTimeOffset.Parse("2026-10-01T10:00:00Z"), repository.WindowFrom);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T11:00:00Z"), repository.WindowTo);
        Assert.Equal("BTCUSDT", repository.WindowSymbol);
        Assert.Equal("BTCUSDT", payload.Symbol);
    }

    [Fact]
    public async Task Window_RejectsMissingOrInvalidBoundsWithoutQueryingRepository()
    {
        var repository = new CapturingRepository();
        var controller = new SignalTransitionMetricsController(repository);
        var at = DateTimeOffset.Parse("2026-10-01T10:00:00Z");

        var missing = await controller.GetWindowAsync(
            null,
            at,
            cancellationToken: CancellationToken.None);
        var missingProblem = Assert.IsType<ObjectResult>(missing.Result);
        Assert.Equal(400, missingProblem.StatusCode);

        var reversed = await controller.GetWindowAsync(
            at,
            at,
            cancellationToken: CancellationToken.None);
        var reversedProblem = Assert.IsType<ObjectResult>(reversed.Result);
        Assert.Equal(400, reversedProblem.StatusCode);

        Assert.Equal(0, repository.WindowCallCount);
    }

    [Fact]
    public async Task Series_NormalizesUtcWindowBucketAndSymbol()
    {
        var repository = new CapturingRepository();
        var controller = new SignalTransitionMetricsController(repository);

        var action = await controller.GetSeriesAsync(
            DateTimeOffset.Parse("2026-10-01T12:00:00+02:00"),
            DateTimeOffset.Parse("2026-10-01T13:00:00+02:00"),
            " 5M ",
            " btcusdt ",
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var payload = Assert.IsType<SignalTransitionMetricsSeriesSnapshot>(ok.Value);

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
        var repository = new CapturingRepository();
        var controller = new SignalTransitionMetricsController(repository);

        var action = await controller.GetSeriesAsync(
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
        var repository = new CapturingRepository();
        var controller = new SignalTransitionMetricsController(repository);
        var from = DateTimeOffset.Parse("2026-10-01T00:00:00Z");

        var action = await controller.GetSeriesAsync(
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
        var repository = new CapturingRepository();
        var controller = new SignalTransitionMetricsController(repository);
        var from = DateTimeOffset.Parse("2026-10-01T00:00:00Z");

        var action = await controller.GetSeriesAsync(
            from,
            from.AddMinutes(500),
            "1m",
            cancellationToken: CancellationToken.None);

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.Equal(1, repository.SeriesCallCount);
    }

    [Fact]
    public async Task BySymbol_NormalizesWindowAndUsesDefaultLimit()
    {
        var repository = new CapturingRepository();
        var controller = new SignalTransitionMetricsController(repository);

        var action = await controller.GetBySymbolAsync(
            DateTimeOffset.Parse("2026-10-01T12:00:00+02:00"),
            DateTimeOffset.Parse("2026-10-01T13:00:00+02:00"),
            cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var payload = Assert.IsType<SignalTransitionMetricsBySymbolSnapshot>(ok.Value);

        Assert.Equal(DateTimeOffset.Parse("2026-10-01T10:00:00Z"), repository.BySymbolFrom);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T11:00:00Z"), repository.BySymbolTo);
        Assert.Equal(20, repository.BySymbolLimit);
        Assert.Equal(20, payload.Limit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task BySymbol_RejectsLimitOutsideAllowedRange(int limit)
    {
        var repository = new CapturingRepository();
        var controller = new SignalTransitionMetricsController(repository);

        var action = await controller.GetBySymbolAsync(
            DateTimeOffset.Parse("2026-10-01T10:00:00Z"),
            DateTimeOffset.Parse("2026-10-01T11:00:00Z"),
            limit,
            CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(400, problem.StatusCode);
        Assert.Equal(0, repository.BySymbolCallCount);
    }

    private sealed class CapturingRepository : ISignalTransitionMetricsRepository
    {
        public int WindowCallCount { get; private set; }
        public DateTimeOffset? WindowFrom { get; private set; }
        public DateTimeOffset? WindowTo { get; private set; }
        public string? WindowSymbol { get; private set; }

        public int SeriesCallCount { get; private set; }
        public DateTimeOffset? SeriesFrom { get; private set; }
        public DateTimeOffset? SeriesTo { get; private set; }
        public string? SeriesBucket { get; private set; }
        public TimeSpan? SeriesBucketSize { get; private set; }
        public string? SeriesSymbol { get; private set; }

        public int BySymbolCallCount { get; private set; }
        public DateTimeOffset? BySymbolFrom { get; private set; }
        public DateTimeOffset? BySymbolTo { get; private set; }
        public int? BySymbolLimit { get; private set; }

        public Task<SignalTransitionMetricsWindowSnapshot> GetWindowAsync(
            DateTimeOffset fromInclusive,
            DateTimeOffset toExclusive,
            string? symbol = null,
            CancellationToken cancellationToken = default)
        {
            WindowCallCount++;
            WindowFrom = fromInclusive;
            WindowTo = toExclusive;
            WindowSymbol = symbol;

            return Task.FromResult(new SignalTransitionMetricsWindowSnapshot(
                DateTimeOffset.UtcNow,
                fromInclusive,
                toExclusive,
                symbol,
                0,
                0,
                0));
        }

        public Task<SignalTransitionMetricsSeriesSnapshot> GetSeriesAsync(
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

            return Task.FromResult(new SignalTransitionMetricsSeriesSnapshot(
                DateTimeOffset.UtcNow,
                fromInclusive,
                toExclusive,
                bucket,
                symbol,
                Array.Empty<SignalTransitionMetricsSeriesBucket>()));
        }

        public Task<SignalTransitionMetricsBySymbolSnapshot> GetBySymbolAsync(
            DateTimeOffset fromInclusive,
            DateTimeOffset toExclusive,
            int limit,
            CancellationToken cancellationToken = default)
        {
            BySymbolCallCount++;
            BySymbolFrom = fromInclusive;
            BySymbolTo = toExclusive;
            BySymbolLimit = limit;

            return Task.FromResult(new SignalTransitionMetricsBySymbolSnapshot(
                DateTimeOffset.UtcNow,
                fromInclusive,
                toExclusive,
                limit,
                false,
                Array.Empty<SignalTransitionMetricsBySymbolItem>()));
        }
    }
}
