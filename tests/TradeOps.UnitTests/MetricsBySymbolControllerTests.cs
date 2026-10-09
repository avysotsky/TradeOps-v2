using Microsoft.AspNetCore.Mvc;
using TradeOps.Api.Controllers;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class MetricsBySymbolControllerTests
{
    [Fact]
    public async Task BySymbol_NormalizesWindowAndUsesDefaultLimit()
    {
        var repository = new CapturingRepository();
        var controller = new MetricsBySymbolController(repository);
        var from = DateTimeOffset.Parse("2026-10-01T12:00:00+02:00");
        var to = DateTimeOffset.Parse("2026-10-01T13:00:00+02:00");

        var action = await controller.GetExecutionBySymbolAsync(
            from,
            to,
            cancellationToken: CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var payload = Assert.IsType<ExecutionMetricsBySymbolSnapshot>(ok.Value);

        Assert.Equal(DateTimeOffset.Parse("2026-10-01T10:00:00Z"), repository.From);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T11:00:00Z"), repository.To);
        Assert.Equal(50, repository.Limit);
        Assert.Equal(50, payload.Limit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task BySymbol_RejectsLimitOutsideAllowedRange(int limit)
    {
        var repository = new CapturingRepository();
        var controller = new MetricsBySymbolController(repository);

        var action = await controller.GetExecutionBySymbolAsync(
            DateTimeOffset.Parse("2026-10-01T10:00:00Z"),
            DateTimeOffset.Parse("2026-10-01T11:00:00Z"),
            limit,
            CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(400, problem.StatusCode);
        Assert.Equal(0, repository.CallCount);
    }

    [Fact]
    public async Task BySymbol_RejectsInvalidWindow()
    {
        var repository = new CapturingRepository();
        var controller = new MetricsBySymbolController(repository);
        var from = DateTimeOffset.Parse("2026-10-01T10:00:00Z");

        var action = await controller.GetExecutionBySymbolAsync(
            from,
            from,
            cancellationToken: CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(400, problem.StatusCode);
        Assert.Equal(0, repository.CallCount);
    }

    private sealed class CapturingRepository : IExecutionMetricsBySymbolRepository
    {
        public int CallCount { get; private set; }
        public DateTimeOffset? From { get; private set; }
        public DateTimeOffset? To { get; private set; }
        public int? Limit { get; private set; }

        public Task<ExecutionMetricsBySymbolSnapshot> GetAsync(
            DateTimeOffset fromInclusive,
            DateTimeOffset toExclusive,
            int limit,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            From = fromInclusive;
            To = toExclusive;
            Limit = limit;

            return Task.FromResult(new ExecutionMetricsBySymbolSnapshot(
                DateTimeOffset.UtcNow,
                fromInclusive,
                toExclusive,
                limit,
                false,
                Array.Empty<ExecutionMetricsBySymbolItem>()));
        }
    }
}
