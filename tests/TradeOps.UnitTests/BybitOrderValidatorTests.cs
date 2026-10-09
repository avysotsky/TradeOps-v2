using TradeOps.Application.Models;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Bybit;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class BybitOrderValidatorTests
{
    private static readonly BybitInstrumentDto Instrument = new()
    {
        Symbol = "BTCUSDT",
        Status = "Trading",
        PriceFilter = new BybitPriceFilterDto
        {
            MinPrice = "1",
            MaxPrice = "1000000",
            TickSize = "0.10"
        },
        LotSizeFilter = new BybitLotSizeFilterDto
        {
            MinNotionalValue = "5",
            MinOrderQty = "0.001",
            MaxOrderQty = "10",
            MaxMarketOrderQty = "5",
            QuantityStep = "0.001"
        }
    };

    [Fact]
    public void Validate_AcceptsAlignedMarketOrder()
    {
        var request = new PlaceOrderRequest(
            "trd-d0f8625f2ad444eba1ec22acbfbb2e58",
            "BTCUSDT",
            OrderSide.Buy,
            OrderType.Market,
            0.002m);

        Assert.Null(BybitOrderValidator.Validate(request, Instrument));
    }

    [Fact]
    public void Validate_RejectsQuantityThatIsNotAlignedToStep()
    {
        var request = new PlaceOrderRequest(
            "trd-d0f8625f2ad444eba1ec22acbfbb2e58",
            "BTCUSDT",
            OrderSide.Buy,
            OrderType.Market,
            0.0015m);

        var error = BybitOrderValidator.Validate(request, Instrument);

        Assert.Contains("qtyStep", error);
    }

    [Fact]
    public void Validate_RejectsLimitPriceThatIsNotAlignedToTick()
    {
        var request = new PlaceOrderRequest(
            "trd-d0f8625f2ad444eba1ec22acbfbb2e58",
            "BTCUSDT",
            OrderSide.Buy,
            OrderType.Limit,
            0.002m,
            64000.05m);

        var error = BybitOrderValidator.Validate(request, Instrument);

        Assert.Contains("tickSize", error);
    }

    [Fact]
    public void Validate_RejectsLimitOrderBelowMinimumNotional()
    {
        var request = new PlaceOrderRequest(
            "trd-d0f8625f2ad444eba1ec22acbfbb2e58",
            "BTCUSDT",
            OrderSide.Buy,
            OrderType.Limit,
            0.001m,
            1000m);

        var error = BybitOrderValidator.Validate(request, Instrument);

        Assert.Contains("notional", error, StringComparison.OrdinalIgnoreCase);
    }
}
