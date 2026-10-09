using TradeOps.Api.Contracts;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class TradingSignalRequestValidatorTests
{
    [Fact]
    public void ValidRequest_HasNoValidationErrors()
    {
        var request = new CreateTradingSignalRequest(
            " BTCUSDT ",
            OrderSide.Buy,
            0.001m,
            RiskPercent: 1.25m,
            StopLoss: 49000.123456789012m,
            TakeProfit: 51000.123456789012m,
            Source: new string('s', 100),
            SignalId: Guid.NewGuid());

        Assert.Empty(TradingSignalRequestValidator.Validate(request));
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void InvalidRequest_ReturnsExpectedField(
        CreateTradingSignalRequest request,
        string field)
    {
        var errors = TradingSignalRequestValidator.Validate(request);

        Assert.Contains(field, errors.Keys);
    }

    public static IEnumerable<object[]> InvalidRequests()
    {
        var id = Guid.NewGuid();

        yield return [New(symbol: "   ", signalId: id), "Symbol"];
        yield return [New(symbol: new string('A', 51), signalId: id), "Symbol"];
        yield return [New(side: (OrderSide)999, signalId: id), "Side"];
        yield return [New(quantity: 0m, signalId: id), "Quantity"];
        yield return [New(quantity: -1m, signalId: id), "Quantity"];
        yield return [New(quantity: 0.0000000000001m, signalId: id), "Quantity"];
        yield return [New(quantity: 10_000_000_000_000_000m, signalId: id), "Quantity"];
        yield return [New(riskPercent: 0m, signalId: id), "RiskPercent"];
        yield return [New(riskPercent: 100.00000001m, signalId: id), "RiskPercent"];
        yield return [New(riskPercent: 1.123456789m, signalId: id), "RiskPercent"];
        yield return [New(stopLoss: 0m, signalId: id), "StopLoss"];
        yield return [New(stopLoss: 1.1234567890123m, signalId: id), "StopLoss"];
        yield return [New(takeProfit: -1m, signalId: id), "TakeProfit"];
        yield return [New(takeProfit: 10_000_000_000_000_000m, signalId: id), "TakeProfit"];
        yield return [New(source: new string('s', 101), signalId: id), "Source"];
        yield return [New(signalId: Guid.Empty), "SignalId"];
    }

    private static CreateTradingSignalRequest New(
        string symbol = "BTCUSDT",
        OrderSide side = OrderSide.Buy,
        decimal quantity = 0.001m,
        decimal? riskPercent = null,
        decimal? stopLoss = null,
        decimal? takeProfit = null,
        string? source = "test",
        Guid? signalId = null) =>
        new(
            symbol,
            side,
            quantity,
            riskPercent,
            stopLoss,
            takeProfit,
            source,
            signalId);
}
