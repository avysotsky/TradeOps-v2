using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class PnLAccountingCalculatorTests
{
    [Fact]
    public void CalculateDaily_SettlementCurrencyFees_ProducesNetRealizedPnl()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var fills = new[]
        {
            new PositionFill("BTCUSDT", OrderSide.Buy, 1m, 100m, now.AddHours(-2), "open", 0.50m, "USDT"),
            new PositionFill("BTCUSDT", OrderSide.Sell, 1m, 120m, now.AddHours(-1), "close", 0.75m, "usdt")
        };

        var result = PnLAccountingCalculator.CalculateDaily(fills, "USDT", now);

        Assert.Equal(20m, result.GrossRealizedPnL);
        Assert.Equal(1.25m, result.SettlementFees);
        Assert.Equal(18.75m, result.NetRealizedPnL);
        Assert.True(result.IsComplete);
        Assert.Empty(result.UnconvertedFees);
    }

    [Fact]
    public void CalculateDaily_UnsupportedFeeCurrency_LeavesNetPnlUnresolved()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var fills = new[]
        {
            new PositionFill("BTCUSDT", OrderSide.Buy, 1m, 100m, now.AddHours(-2), "open", 0.00001m, "BTC"),
            new PositionFill("BTCUSDT", OrderSide.Sell, 1m, 120m, now.AddHours(-1), "close", 0.50m, "USDT")
        };

        var result = PnLAccountingCalculator.CalculateDaily(fills, "USDT", now);

        Assert.Equal(20m, result.GrossRealizedPnL);
        Assert.Equal(0.50m, result.SettlementFees);
        Assert.Null(result.NetRealizedPnL);
        Assert.False(result.IsComplete);
        var fee = Assert.Single(result.UnconvertedFees);
        Assert.Equal("BTC", fee.Currency);
        Assert.Equal(0.00001m, fee.Amount);
    }

    [Fact]
    public void CalculateDaily_PositionOpenedYesterdayClosedToday_UsesTodayGrossAndTodayFeesOnly()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var dayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var fills = new[]
        {
            new PositionFill("BTCUSDT", OrderSide.Buy, 1m, 100m, dayStart.AddHours(-1), "open-yesterday", 0.40m, "USDT"),
            new PositionFill("BTCUSDT", OrderSide.Sell, 1m, 120m, dayStart.AddHours(1), "close-today", 0.60m, "USDT")
        };

        var result = PnLAccountingCalculator.CalculateDaily(fills, "USDT", now);

        Assert.Equal(20m, result.GrossRealizedPnL);
        Assert.Equal(0.60m, result.SettlementFees);
        Assert.Equal(19.40m, result.NetRealizedPnL);
    }

    [Fact]
    public void CalculateDaily_NegativeSettlementFee_IsTreatedAsRebate()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var fills = new[]
        {
            new PositionFill("BTCUSDT", OrderSide.Buy, 1m, 100m, now.AddHours(-2), "open"),
            new PositionFill("BTCUSDT", OrderSide.Sell, 1m, 120m, now.AddHours(-1), "close", -0.25m, "USDT")
        };

        var result = PnLAccountingCalculator.CalculateDaily(fills, "USDT", now);

        Assert.Equal(-0.25m, result.SettlementFees);
        Assert.Equal(20.25m, result.NetRealizedPnL);
    }
}
