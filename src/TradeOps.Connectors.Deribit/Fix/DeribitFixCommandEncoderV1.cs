using QuickFix;
using TradeOps.Application.Oms;
using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;

namespace TradeOps.Connectors.Deribit.Fix;

internal sealed record DeribitFixInstrumentV1(string Symbol,string Kind,string InstrumentType,string Currency,string QuoteCurrency,
    decimal ContractUsd,decimal AmountStep,DateTimeOffset ObservedAtUtc,DateTimeOffset ExpiresAtUtc);
internal sealed record DeribitFixCancelBindingV1(OmsJournalCommandRequest Original,DeribitFixExecutionReportV1 Acknowledgement);

internal static class DeribitFixCommandEncoderV1
{
    internal static string ClientId(OmsJournalCommandRequest command)=>"to-fix-"+command.CommandId.Value.ToString("N");
    internal static Message Encode(OmsJournalCommandRequest command,OmsFixSessionKeyV1 session,DeribitFixInstrumentV1 metadata,
        DateTimeOffset now,DeribitFixCancelBindingV1? parent=null)
    {
        command.Validate();session.Validate();var p=command.Payload;
        if(command.Scope!=session.Scope||metadata.Symbol!=p.Instrument||metadata.Kind!="future"||metadata.InstrumentType!="reversed"||
            metadata.Currency is not("BTC" or "ETH")||metadata.QuoteCurrency!="USD"||metadata.ContractUsd!=1||metadata.AmountStep<=0||
            metadata.ObservedAtUtc.Offset!=TimeSpan.Zero||now<metadata.ObservedAtUtc||now-metadata.ObservedAtUtc>TimeSpan.FromSeconds(5)||metadata.ExpiresAtUtc<=now||
            p.QuantityUnit!=OmsQuantityUnit.QuoteAsset||p.Quantity%metadata.AmountStep!=0||p.OrderType!=OrderType.Limit||p.LimitPrice is not>0||p.TriggerPrice is not null||
            p.TimeInForce is not("GTC" or "good_til_cancelled")||p.FixClOrdId is not null||p.FixOrigClOrdId is not null||
            p.TargetClientOrderId is null||p.TargetClientOrderId.Length>64||p.TargetClientOrderId.Any(c=>c<32||c>126))
            throw new InvalidOperationException("Unsupported or stale FIX command terms/units.");
        var m=new Message();
        if(p.Kind==OmsJournalCommandKind.Place)
        {
            if(parent is not null||p.TargetExchangeOrderId is not null||command.ExpectedAggregateVersion.Value!=0)throw new InvalidOperationException("Invalid FIX placement identity.");
            m.Header.SetField(new QuickFix.Fields.MsgType("D"));m.SetField(new QuickFix.Fields.ClOrdID(ClientId(command)));
            m.SetField(new QuickFix.Fields.Symbol(p.Instrument));m.SetField(new QuickFix.Fields.Side(p.Side==OrderSide.Buy?'1':'2'));
            m.SetField(new QuickFix.Fields.OrderQty(p.Quantity));m.SetField(new QuickFix.Fields.IntField(854,0));
            m.SetField(new QuickFix.Fields.OrdType('2'));m.SetField(new QuickFix.Fields.Price(p.LimitPrice.Value));m.SetField(new QuickFix.Fields.TimeInForce('1'));
            var flags=(p.PostOnly?"6A":"")+(p.ReduceOnly?"E":"");if(flags.Length>0)m.SetField(new QuickFix.Fields.ExecInst(flags));
            m.SetField(new QuickFix.Fields.StringField(100010,p.TargetClientOrderId));return m;
        }
        if(p.Kind is not(OmsJournalCommandKind.Cancel or OmsJournalCommandKind.Replace)||parent is null)throw new InvalidOperationException("Unbound native mutation refused.");
        var original=parent.Original;var o=original.Payload;var ack=parent.Acknowledgement;ack.Validate();original.Validate();
        if(p.Kind==OmsJournalCommandKind.Replace||original.Payload.Kind==OmsJournalCommandKind.Replace)
        {
            var binding=new DeribitFixCommandBindingV1(2,command,session,ack.ConnectionEpoch,now,metadata.Currency,metadata.ContractUsd,
                DeribitFixCommandBindingV1.ClientId(command),ack);
            try{binding.Validate();}catch(ArgumentException){throw new InvalidOperationException("Unsupported native FIX mutation terms.");}
            if(original.Scope!=command.Scope||original.LocalOrderId!=command.LocalOrderId||original.IntentId!=command.IntentId||
                command.ExpectedAggregateVersion.Value!=original.ExpectedAggregateVersion.Value+1||
                p.TargetClientOrderId!=o.TargetClientOrderId||p.Instrument!=o.Instrument||p.Side!=o.Side||p.Quantity!=o.Quantity||
                p.QuantityUnit!=o.QuantityUnit||p.OrderType!=o.OrderType||p.PostOnly!=o.PostOnly||p.ReduceOnly!=o.ReduceOnly||p.TimeInForce!=o.TimeInForce||
                ack.Price!=o.LimitPrice)throw new InvalidOperationException("FIX mutation does not follow the current own revision.");
            m.Header.SetField(new QuickFix.Fields.MsgType(p.Kind==OmsJournalCommandKind.Replace?"G":"F"));
            m.SetField(new QuickFix.Fields.OrigClOrdID(ack.ServerAssignedClOrdId!));
            if(p.Kind==OmsJournalCommandKind.Replace)
            {
                m.SetField(new QuickFix.Fields.Symbol(p.Instrument));m.SetField(new QuickFix.Fields.Side(p.Side==OrderSide.Buy?'1':'2'));
                m.SetField(new QuickFix.Fields.OrderQty(p.Quantity));m.SetField(new QuickFix.Fields.IntField(854,0));
                m.SetField(new QuickFix.Fields.OrdType('2'));m.SetField(new QuickFix.Fields.Price(p.LimitPrice!.Value));
                var flags=(p.PostOnly?"6A":"")+(p.ReduceOnly?"E":"");if(flags.Length>0)m.SetField(new QuickFix.Fields.ExecInst(flags));
            }
            return m;
        }
        if(original.Scope!=command.Scope||original.LocalOrderId!=command.LocalOrderId||original.IntentId!=command.IntentId||
            original.Payload.Kind!=OmsJournalCommandKind.Place||command.ExpectedAggregateVersion.Value!=1||ack.Session.Scope!=command.Scope||
            ack.NativeOrderId is null||ack.ServerAssignedClOrdId is null||ack.NativeOrderId!=p.TargetExchangeOrderId||ack.OriginalClientClOrdId!=ClientId(original)||
            ack.Instrument!=p.Instrument||ack.Side!=p.Side||!ack.HasUnambiguousUsdAmounts(metadata.ContractUsd)||ack.OrderQty!=p.Quantity||ack.Price!=p.LimitPrice||
            p.TargetClientOrderId!=o.TargetClientOrderId||p.Instrument!=o.Instrument||p.Side!=o.Side||p.Quantity!=o.Quantity||p.QuantityUnit!=o.QuantityUnit||
            p.LimitPrice!=o.LimitPrice||p.PostOnly!=o.PostOnly||p.ReduceOnly!=o.ReduceOnly||p.TimeInForce!=o.TimeInForce||
            ack.Label!=o.TargetClientOrderId||ack.OrderType!="2"||ack.TimeInForce is not(null or "1")||ack.RejectReason!=0||
            ack.ExecutionInstructions.Contains('6')!=o.PostOnly||ack.ExecutionInstructions.Contains('E')!=o.ReduceOnly||ack.OrderStatus is not("0" or "1"))
            throw new InvalidOperationException("FIX cancellation does not belong to the original native order.");
        m.Header.SetField(new QuickFix.Fields.MsgType("F"));m.SetField(new QuickFix.Fields.OrigClOrdID(ack.ServerAssignedClOrdId));
        // Deribit F tag 11 is the ORIGINAL client ID, not a new cancel request ID. Use native tag 41 only.
        return m;
    }
}
