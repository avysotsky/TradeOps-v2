using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class PositionPnLCalculatorTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-01T07:00:00Z");

    [Fact]
    public void Calculate_AddsToLong_UsesWeightedAverageAndUnrealizedPnl()
    {
        var fills = new[]
        {
            Fill(OrderSide.Buy, 1m, 100m, 0),
            Fill(OrderSide.Buy, 1m, 120m, 1)
        };

        var state = PositionPnLCalculator.Calculate("BTCUSDT", fills, 130m, T0.AddMinutes(2));

        Assert.Equal(OrderSide.Buy, state.Side);
        Assert.Equal(2m, state.Quantity);
        Assert.Equal(110m, state.AverageEntryPrice);
        Assert.Equal(0m, state.RealizedPnL);
        Assert.Equal(40m, state.UnrealizedPnL);
        Assert.Equal(40m, state.TotalPnL);
    }

    [Fact]
    public void Calculate_PartialClose_RealizesPnlAndKeepsEntryPrice()
    {
        var fills = new[]
        {
            Fill(OrderSide.Buy, 2m, 100m, 0),
            Fill(OrderSide.Sell, 1m, 120m, 1)
        };

        var state = PositionPnLCalculator.Calculate("BTCUSDT", fills, 110m, T0.AddMinutes(2));

        Assert.Equal(OrderSide.Buy, state.Side);
        Assert.Equal(1m, state.Quantity);
        Assert.Equal(100m, state.AverageEntryPrice);
        Assert.Equal(20m, state.RealizedPnL);
        Assert.Equal(10m, state.UnrealizedPnL);
        Assert.Equal(30m, state.TotalPnL);
    }

    [Fact]
    public void Calculate_Reversal_ClosesOldPositionAndUsesReversalFillAsNewEntry()
    {
        var fills = new[]
        {
            Fill(OrderSide.Buy, 1m, 100m, 0),
            Fill(OrderSide.Sell, 2m, 90m, 1)
        };

        var state = PositionPnLCalculator.Calculate("BTCUSDT", fills, 80m, T0.AddMinutes(2));

        Assert.Equal(OrderSide.Sell, state.Side);
        Assert.Equal(1m, state.Quantity);
        Assert.Equal(90m, state.AverageEntryPrice);
        Assert.Equal(-10m, state.RealizedPnL);
        Assert.Equal(10m, state.UnrealizedPnL);
        Assert.Equal(0m, state.TotalPnL);
    }

    private static PositionFill Fill(
        OrderSide side,
        decimal quantity,
        decimal price,
        int seconds) => new(
            "BTCUSDT",
            side,
            quantity,
            price,
            T0.AddSeconds(seconds),
            $"exec-{seconds:D3}");
}
