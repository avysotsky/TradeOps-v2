using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;
using static TradeOps.UnitTests.DeribitOrderRecoveryProjectorTests;

namespace TradeOps.UnitTests;

internal static class DeribitCommandReceiptFixtures
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 5, 18, 0, 0, TimeSpan.Zero);
    internal static readonly DeribitPrivateInstrument Metadata = new("ETH-PERPETUAL", "future", "ETH", OmsPrivateAmountUnit.Usd, "USD");
    internal static OmsJournalCommandRequest Command() => new(Account, new(Guid.NewGuid()), new(Guid.NewGuid()),
        new(Guid.NewGuid()), new(0), new(OmsJournalCommandKind.Place, "ETH-PERPETUAL", OrderSide.Buy, OrderType.Limit,
            1, OmsQuantityUnit.QuoteAsset, 2000, null, "good_til_cancelled", false, true, "fixture", "operator", "risk", From));

    internal static DeribitJsonRpcCommandReceipt Receipt(OmsJournalCommandRequest command, string nativeId = "native-1",
        long rpcId = 1, DateTimeOffset? sent = null, Guid? session = null)
    {
        var at = sent ?? Now.AddSeconds(-30);
        var p = command.Payload;
        var order = new OmsPrivateVenueFacts(OmsJournalEventKind.Order, "ETH", p.Instrument, nativeId, p.Side,
            p.Quantity, OmsPrivateAmountUnit.Usd, "USD", DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()),
            OrderState: p.Kind == OmsJournalCommandKind.Cancel ? "cancelled" : "open", FilledNativeAmount: 0,
            PriceKind: OmsPrivatePriceKind.Numeric, Price: p.LimitPrice, ClientLabel: p.TargetClientOrderId);
        return new(command, 1, session ?? Guid.NewGuid(), rpcId,
            p.Kind == OmsJournalCommandKind.Cancel ? "private/cancel" : p.Side == OrderSide.Buy ? "private/buy" : "private/sell",
            at, at.AddMilliseconds(1), "future", order, "limit", "good_til_cancelled", p.PostOnly, p.ReduceOnly);
    }

    internal static JsonElement Response(DeribitJsonRpcCommandReceipt receipt)
    {
        var f = receipt.Order;
        var order = new { instrument_name = f.NativeInstrument, order_id = f.ExchangeOrderId,
            direction = f.Side == OrderSide.Buy ? "buy" : "sell", amount = f.NativeAmount, order_state = f.OrderState,
            filled_amount = f.FilledNativeAmount, price = f.Price, last_update_timestamp = f.VenueTimestampUtc.ToUnixTimeMilliseconds(),
            order_type = receipt.OrderType, time_in_force = receipt.TimeInForce, post_only = receipt.PostOnly,
            reduce_only = receipt.ReduceOnly, label = f.ClientLabel };
        return JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = receipt.RpcRequestId,
            result = receipt.Command.Payload.Kind == OmsJournalCommandKind.Place ? (object)new { order, trades = Array.Empty<object>() } : order });
    }

    internal static DeribitJsonRpcCommandReceipt Parse(DeribitJsonRpcCommandReceipt context, JsonElement response) =>
        DeribitJsonRpcCommandReceiptParser.Parse(context.Command, Metadata, context.SessionId,
            context.RpcRequestId, context.Method, context.SentAtUtc, context.ReceivedAtUtc, response);

    internal static OmsJournalCommandRequest Cancel(OmsJournalCommandRequest place, string nativeId = "native-1") =>
        place with { CommandId = new(Guid.NewGuid()), ExpectedAggregateVersion = new(1), Payload = place.Payload with {
            Kind = OmsJournalCommandKind.Cancel, TargetExchangeOrderId = nativeId } };
}
