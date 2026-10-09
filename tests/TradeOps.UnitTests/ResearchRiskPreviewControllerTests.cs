using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using TradeOps.Api.Controllers;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class ResearchRiskPreviewControllerTests
{
    private static ResearchRiskPreviewController Controller(bool enabled) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> {
                ["ResearchPreview:Enabled"] = enabled.ToString(),
                ["OperatorApi:Authentication:Enabled"] = "true"
            }).Build());

    private static SnapshotRiskPreviewRequest Request(bool enabled) => new(
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        "SAMP", OrderSide.Buy, 2m,
        new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        new RiskSettings
        {
            AllowedSymbols = new HashSet<string> { "SAMP" },
            MaxOrderSize = 10m,
            MaxPositionSize = 10m,
            MaxDailyLoss = 100m,
            MaxOpenPositions = 5
        },
        new ResearchRiskControlsRequest(
            enabled, false, null, "USDT", 0m, 0m, 0m,
            Array.Empty<UnconvertedFee>(), 0,
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero)),
        Array.Empty<Position>());

    [Fact]
    public async Task OptInRequired()
    {
        var result = await Controller(false).Preview(Request(true), CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task AllowedMatchesDirectSnapshotRisk()
    {
        var result = await Controller(true).Preview(Request(true), CancellationToken.None);
        var output = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SnapshotRiskPreviewResponse>(output.Value);
        Assert.True(body.Allowed);
        Assert.Equal(1, body.SchemaVersion);
    }

    [Fact]
    public async Task RejectedSnapshotStaysNonMutating()
    {
        var result = await Controller(true).Preview(Request(false), CancellationToken.None);
        var output = Assert.IsType<OkObjectResult>(result);
        Assert.False(Assert.IsType<SnapshotRiskPreviewResponse>(output.Value).Allowed);
    }

    [Fact]
    public async Task InvalidInputRejected()
    {
        var invalid = Request(true) with { Quantity = 0m };
        var result = await Controller(true).Preview(invalid, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }
}
