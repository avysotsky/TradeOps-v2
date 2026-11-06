using TradeOps.Domain.Oms;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Oms;

/// <summary>New versioned FIX dispatch binding. Retained before wire, never a send capability.
/// Native account pin and public instrument metadata are verified by the composing host.</summary>
public sealed record DeribitFixCommandBindingV1(int SchemaVersion, OmsJournalCommandRequest Command,
    OmsFixSessionKeyV1 Session, Guid ConnectionEpoch, DateTimeOffset PreparedAtUtc,
    string Currency, decimal VerifiedContractUsd, string ClientClOrdId,
    DeribitFixExecutionReportV1? Parent)
{
    public static string ClientId(OmsJournalCommandRequest command)=>"to-fix-"+command.CommandId.Value.ToString("N");
    public void Validate()
    {
        Command.Validate();Session.Validate();Parent?.Validate();var p=Command.Payload;
        if(SchemaVersion!=1||Session.Scope!=Command.Scope||ConnectionEpoch==Guid.Empty||PreparedAtUtc.Offset!=TimeSpan.Zero||
            PreparedAtUtc< p.RequestedAtUtc||Currency is not("ETH" or "BTC")||VerifiedContractUsd!=1||ClientClOrdId!=ClientId(Command)||
            p.QuantityUnit!=OmsQuantityUnit.QuoteAsset||p.OrderType!=OrderType.Limit||p.LimitPrice is not >0||p.TriggerPrice is not null||
            p.FixClOrdId is not null||p.FixOrigClOrdId is not null||p.TimeInForce is not("GTC" or "good_til_cancelled")||
            p.TargetClientOrderId is null||p.TargetClientOrderId.Length>64||p.TargetClientOrderId.Any(c=>c<32||c>126)||
            p.Kind is not(OmsJournalCommandKind.Place or OmsJournalCommandKind.Cancel)||
            p.Kind==OmsJournalCommandKind.Place&&(Parent is not null||p.TargetExchangeOrderId is not null||Command.ExpectedAggregateVersion.Value!=0)||
            p.Kind==OmsJournalCommandKind.Cancel&&(Command.ExpectedAggregateVersion.Value!=1||p.TargetExchangeOrderId is null||Parent is null||Parent.NativeOrderId is null||Parent.Session.Scope!=Command.Scope||
                Parent.NativeOrderId!=p.TargetExchangeOrderId||Parent.ServerAssignedClOrdId is null||Parent.OrderStatus is not("0" or "1")||
                !Parent.HasUnambiguousUsdAmounts(VerifiedContractUsd)||Parent.Instrument!=p.Instrument||Parent.Side!=p.Side||
                Parent.OrderQty!=p.Quantity||Parent.Price!=p.LimitPrice||Parent.Label!=p.TargetClientOrderId||
                Parent.ExecutionInstructions.Contains('6')!=p.PostOnly||Parent.ExecutionInstructions.Contains('A')!=p.PostOnly||Parent.ExecutionInstructions.Contains('E')!=p.ReduceOnly))
            throw new ArgumentException("Invalid native FIX command binding.");
    }

    public DeribitNativeCommandView? Match(DeribitFixExecutionReportV1 report)
    {
        Validate();report.Validate();var p=Command.Payload;
        if(report.Session!=Session||report.ReceivedAtUtc<PreparedAtUtc||report.TransactTimeUtc<PreparedAtUtc.AddSeconds(-5)||
            report.Instrument!=p.Instrument||report.Side!=p.Side||report.NativeOrderId is null||report.ServerAssignedClOrdId is null||
            !report.HasUnambiguousUsdAmounts(VerifiedContractUsd)||report.OrderQty!=p.Quantity||report.Price!=p.LimitPrice||
            report.OrderType!="2"||report.TimeInForce is not(null or "1")||report.Label!=p.TargetClientOrderId||
            report.OrderStatus!="8"&&report.RejectReason!=0||report.OrderStatus=="8"&&(report.CumQty!=0||report.LeavesQty!=0)||
            report.OrderStatus=="4"&&report.LeavesQty!=0||
            report.ExecutionInstructions.Contains('6')!=p.PostOnly||report.ExecutionInstructions.Contains('A')!=p.PostOnly||report.ExecutionInstructions.Contains('E')!=p.ReduceOnly||
            (p.Kind==OmsJournalCommandKind.Place?report.OriginalClientClOrdId!=ClientClOrdId:
                report.ConnectionEpoch!=ConnectionEpoch||report.NativeOrderId!=Parent!.NativeOrderId||
                report.ServerAssignedClOrdId!=Parent.ServerAssignedClOrdId||report.OrderStatus!="4")) return null;
        var order=new OmsPrivateVenueFacts(OmsJournalEventKind.Order,Currency,report.Instrument,report.NativeOrderId,report.Side,
            report.OrderQty,OmsPrivateAmountUnit.Usd,"USD",report.TransactTimeUtc,OrderState:report.OrderStatus switch
            {"0" or "1"=>"open","2"=>"filled","4"=>"cancelled","8"=>"rejected",_=>throw new ArgumentException("Unknown FIX state.")},
            FilledNativeAmount:report.CumQty,LeavesNativeAmount:report.LeavesQty,PriceKind:OmsPrivatePriceKind.Numeric,
            Price:report.Price,ClientLabel:report.Label);
        order.Validate();return new(Command,order,PreparedAtUtc,report.ReceivedAtUtc,this,report);
    }
}

/// <summary>In-memory common view only. Never persisted as a JSON-RPC receipt, private fill or archived v1 DTO.</summary>
public sealed record DeribitNativeCommandView(OmsJournalCommandRequest Command, OmsPrivateVenueFacts Order,
    DateTimeOffset SentAtUtc, DateTimeOffset ReceivedAtUtc,DeribitFixCommandBindingV1? FixBinding=null,
    DeribitFixExecutionReportV1? FixReport=null,DeribitJsonRpcCommandReceipt? WsReceipt=null)
{
    public static DeribitNativeCommandView FromWs(DeribitJsonRpcCommandReceipt receipt)
    {receipt.Validate();return new(receipt.Command,receipt.Order,receipt.SentAtUtc,receipt.ReceivedAtUtc,WsReceipt:receipt);}
    public void Validate()
    {
        var rebuilt=WsReceipt is not null&&FixBinding is null&&FixReport is null?FromWs(WsReceipt):
            WsReceipt is null&&FixBinding is not null&&FixReport is not null?FixBinding.Match(FixReport):null;
        if(rebuilt!=this)throw new ArgumentException("Invalid native command evidence view.");
    }
}

public static class DeribitNativeCommandEvidenceV1
{
    public static IReadOnlyList<DeribitNativeCommandView> Read(OmsRecoveryLedgerSnapshot snapshot)
    {
        var scope=snapshot.Private.Scope;var commands=snapshot.Commands.ToDictionary(x=>x.Request.CommandId);
        DeribitCommandReceiptSet.Validate(scope,snapshot.Commands,snapshot.Receipts,snapshot.Private.ReadAtUtc);
        var views=snapshot.Receipts.Select(DeribitNativeCommandView.FromWs).ToList();
        var bindings=snapshot.FixBindings??[];var reports=snapshot.FixReports??[];
        if(bindings.Count>10000||reports.Count>10000)throw new ArgumentException("FIX evidence capacity exceeded.");
        var ids=new HashSet<OmsCommandId>(views.Select(x=>x.Command.CommandId));
        foreach(var binding in bindings)
        {
            binding.Validate();
            if(binding.Command.Scope!=scope||binding.PreparedAtUtc>snapshot.Private.ReadAtUtc||
                !commands.TryGetValue(binding.Command.CommandId,out var owner)||owner.Request!=binding.Command||
                owner.Phase==OmsRecoveryDispatchPhase.PersistedNotSent||!ids.Add(binding.Command.CommandId))
                throw new ArgumentException("Invalid native dispatch binding.");
            var matches=reports.Where(x=>x.Execution is not null).Select(x=>binding.Match(x.Execution!)).Where(x=>x is not null)
                .OrderBy(x=>x!.Order.VenueTimestampUtc).ThenBy(x=>x!.ReceivedAtUtc).ToArray();
            if(matches.Length>0)views.Add(matches[0]!);
        }
        foreach(var cancel in bindings.Where(x=>x.Command.Payload.Kind==OmsJournalCommandKind.Cancel))
        {
            var parents=views.Where(x=>x.Command.Payload.Kind==OmsJournalCommandKind.Place&&x.Command.Scope==cancel.Command.Scope&&
                x.Command.LocalOrderId==cancel.Command.LocalOrderId&&x.Command.IntentId==cancel.Command.IntentId&&
                x.Order.ExchangeOrderId==cancel.Command.Payload.TargetExchangeOrderId).ToArray();
            if(parents.Length!=1||parents[0].FixBinding is null||parents[0].FixBinding!.Match(cancel.Parent!) is null||
                !reports.Any(x=>x.Execution==cancel.Parent))throw new ArgumentException("Cancel lacks retained own native placement evidence.");
        }
        foreach(var report in reports)
        {report.Validate();if(report.GetSession().Scope!=scope||(report.Execution?.ReceivedAtUtc??report.CancelReject!.ReceivedAtUtc)>snapshot.Private.ReadAtUtc)
            throw new ArgumentException("Invalid native report snapshot.");}
        if(views.Where(x=>x.Command.Payload.Kind==OmsJournalCommandKind.Place).GroupBy(x=>(x.Order.Currency,x.Order.ExchangeOrderId)).Any(x=>x.Count()!=1))
            throw new ArgumentException("Native placement identity collision.");
        return views.AsReadOnly();
    }
}

public interface IOmsFixAccountOrderTransport : IOmsAccountOrderTransport
{
    Task<DeribitFixCommandBindingV1> PrepareAsync(OmsJournalCommandRequest command,CancellationToken ct);
    Task<DeribitFixExecutionReportV1> SendNativeAsync(OmsJournalCommandRequest command,OmsPhysicalWritePermit permit,CancellationToken ct);
}
