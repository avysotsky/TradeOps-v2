using System.Reflection;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Bybit;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class BybitOrderStatusMappingTests
{
    public static TheoryData<string, OrderStatus> KnownStatuses => new()
    {
        { "New", OrderStatus.Accepted },
        { "PartiallyFilled", OrderStatus.PartiallyFilled },
        { "Filled", OrderStatus.Filled },
        { "Cancelled", OrderStatus.Cancelled },
        { "Canceled", OrderStatus.Cancelled },
        { "PartiallyFilledCanceled", OrderStatus.Cancelled },
        { "PartiallyFilledCancelled", OrderStatus.Cancelled },
        { "Rejected", OrderStatus.Rejected },
        { "Deactivated", OrderStatus.Cancelled }
    };

    [Theory]
    [MemberData(nameof(KnownStatuses))]
    public void MapOrderStatus_MapsKnownBybitStatuses(
        string bybitStatus,
        OrderStatus expected)
    {
        var actual = InvokeMapOrderStatus(bybitStatus);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("Untriggered")]
    [InlineData("Triggered")]
    [InlineData("SomeFutureStatus")]
    [InlineData("")]
    public void MapOrderStatus_MapsUnsupportedStatusesToUnknown(string bybitStatus)
    {
        var actual = InvokeMapOrderStatus(bybitStatus);

        Assert.Equal(OrderStatus.Unknown, actual);
    }

    private static OrderStatus InvokeMapOrderStatus(string status)
    {
        var method = typeof(BybitExchangeClient).GetMethod(
            "MapOrderStatus",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        return (OrderStatus)method.Invoke(null, [status])!;
    }
}
