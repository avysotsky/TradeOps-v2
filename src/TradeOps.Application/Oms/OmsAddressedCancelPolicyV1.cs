using TradeOps.Domain.Oms;
namespace TradeOps.Application.Oms;

public static class OmsAddressedCancelPolicyV1
{
    public static bool Allows(OmsJournalCommandRequest cancel,DeribitJsonRpcCommandReceipt placement,OmsRecoveredAccountStateV1 state,
        OmsRiskPolicyDocumentV1? policy,DateTimeOffset now)
    {try{return Allows(cancel,DeribitNativeCommandView.FromWs(placement),state,policy,now);}catch(ArgumentException){return false;}}
    public static bool Allows(OmsJournalCommandRequest cancel,DeribitNativeCommandView placement,OmsRecoveredAccountStateV1 state,
        OmsRiskPolicyDocumentV1? policy,DateTimeOffset now)
    {
        try
        {
            cancel.Validate();placement.Validate();var p=cancel.Payload;var original=placement.Command;var o=original.Payload;
            if(policy is null||policy.SchemaVersion!=1||policy.Limits.SchemaVersion!=1||policy.Limits.Scope!=cancel.Scope||
                policy.ValidFromUtc.Offset!=TimeSpan.Zero||policy.ValidThroughUtc.Offset!=TimeSpan.Zero||now<policy.ValidFromUtc||now>policy.ValidThroughUtc||
                !policy.AllowedInstruments.Contains(p.Instrument,StringComparer.Ordinal)||cancel.Scope!=state.Scope||cancel.Scope!=original.Scope||
                p.Kind!=OmsJournalCommandKind.Cancel||original.Payload.Kind!=OmsJournalCommandKind.Place||cancel.LocalOrderId!=original.LocalOrderId||
                cancel.IntentId!=original.IntentId||cancel.ExpectedAggregateVersion.Value!=1||original.ExpectedAggregateVersion.Value!=0||
                p.TargetExchangeOrderId!=placement.Order.ExchangeOrderId||p.TargetClientOrderId!=o.TargetClientOrderId||
                p.Instrument!=o.Instrument||p.Side!=o.Side||p.OrderType!=o.OrderType||p.Quantity!=o.Quantity||p.QuantityUnit!=o.QuantityUnit||
                p.LimitPrice!=o.LimitPrice||p.PostOnly!=o.PostOnly||p.ReduceOnly!=o.ReduceOnly||p.TriggerPrice is not null||
                p.TimeInForce is not ("GTC" or "good_til_cancelled")||
                p.FixClOrdId is not null||p.FixOrigClOrdId is not null||now>state.UsableThroughUtc) return false;
            var targets=state.Orders.Where(x=>x.LocalOrderId==original.LocalOrderId&&x.State.Currency==placement.Order.Currency&&x.State.ExchangeOrderId==placement.Order.ExchangeOrderId).ToArray();
            return targets.Length==1&&targets[0].State.NativeState=="open"&&targets[0].State.NativeInstrument==p.Instrument&&
                targets[0].State.NativeAmount==p.Quantity&&targets[0].State.Side==p.Side&&targets[0].State.AmountUnit==placement.Order.AmountUnit&&
                targets[0].State.AmountCurrency==placement.Order.AmountCurrency&&targets[0].State.KnownFilledNativeAmount is { } filled&&filled>=0&&filled<p.Quantity;
        }
        catch(ArgumentException){return false;}
    }
}
