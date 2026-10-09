using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class NonMutatingRiskPreviewTests
{
    private static RiskSettings Settings() => new()
    {
        MaxOrderSize = 100m,
        MaxPositionSize = 100m,
        MaxDailyLoss = 500m,
        MaxOpenPositions = 5,
        AllowedSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SAMP" }
    };

    private static RiskControlSnapshot Controls(bool enabled) => new(
        enabled,
        false,
        null,
        "USDT",
        0m,
        0m,
        0m,
        Array.Empty<UnconvertedFee>(),
        0,
        new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

    private static TradingSignal Signal() => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Symbol = "SAMP",
        Side = OrderSide.Buy,
        RequestedQuantity = 30m
    };

    [Fact]
    public async Task SnapshotRiskAllowedReusesProductionEngine()
    {
        var result = await NonMutatingRiskPreview.CheckAsync(
            Signal(), Settings(), Controls(true), Array.Empty<Position>());
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task SnapshotRiskRejectionDoesNotRequirePersistentRejectionRecorder()
    {
        var result = await NonMutatingRiskPreview.CheckAsync(
            Signal(), Settings(), Controls(false), Array.Empty<Position>());
        Assert.False(result.IsAllowed);
        Assert.NotEmpty(result.Reasons);
    }

    [Fact]
    public async Task PositionLimitUsesSnapshotNotExchange()
    {
        var position = new Position
        {
            Symbol = "SAMP",
            Side = OrderSide.Buy,
            Quantity = 90m,
            AverageEntryPrice = 100m,
            MarkPrice = 100m,
            UnrealizedPnL = 0m
        };
        var result = await NonMutatingRiskPreview.CheckAsync(
            Signal(), Settings(), Controls(true), new[] { position });
        Assert.False(result.IsAllowed);
    }
}
