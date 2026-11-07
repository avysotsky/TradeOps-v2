using TradeOps.Domain.Enums;

namespace TradeOps.Application.Oms;

/// <summary>Price-only native amendment. Full additional reservation is retained until terminal
/// recovery, including unknown/rejected amendments. Quantity/side/flags never change here.</summary>
public static class DeribitFixPriceAmendPolicyV2
{
    public static bool Terms(OmsJournalCommandRequest command,DeribitNativeCommandView parent)
    {
        command.Validate();parent.Validate();var p=command.Payload;var o=parent.Command.Payload;
        return parent.FixReport is not null&&parent.Command.Payload.Kind is OmsJournalCommandKind.Place or OmsJournalCommandKind.Replace&&
            command.Scope==parent.Command.Scope&&command.LocalOrderId==parent.Command.LocalOrderId&&command.IntentId==parent.Command.IntentId&&
            command.ExpectedAggregateVersion.Value==parent.Command.ExpectedAggregateVersion.Value+1&&
            p.Kind==OmsJournalCommandKind.Replace&&p.Instrument=="ETH-PERPETUAL"&&p.TargetExchangeOrderId==parent.Order.ExchangeOrderId&&
            p.TargetClientOrderId==o.TargetClientOrderId&&p.Instrument==o.Instrument&&p.Side==o.Side&&p.OrderType==OrderType.Limit&&p.OrderType==o.OrderType&&
            p.Quantity==o.Quantity&&p.QuantityUnit==o.QuantityUnit&&p.PostOnly==o.PostOnly&&p.ReduceOnly==o.ReduceOnly&&
            p.TimeInForce==o.TimeInForce&&p.LimitPrice is >0&&p.LimitPrice!=o.LimitPrice&&p.TriggerPrice is null&&
            p.FixClOrdId is null&&p.FixOrigClOrdId is null&&parent.Order.OrderState=="open"&&parent.Order.FilledNativeAmount==0;
    }
    public static bool Allows(OmsJournalCommandRequest command,DeribitNativeCommandView parent,OmsRecoveredAccountStateV1 state)
    {
        if(!Terms(command,parent)||state.Scope!=command.Scope)return false;
        var order=state.Orders.SingleOrDefault(x=>x.LocalOrderId==command.LocalOrderId&&x.State.ExchangeOrderId==parent.Order.ExchangeOrderId&&x.State.Currency==parent.Order.Currency);
        return order is not null&&order.State.NativeState=="open"&&order.State.KnownFilledNativeAmount==0&&
            order.State.NativeAmount==command.Payload.Quantity&&order.LastReport.Price==parent.Order.Price&&
            order.LastReport.VenueTimestampUtc>=parent.Order.VenueTimestampUtc&&order.State.NativeInstrument==command.Payload.Instrument;
    }
}
