using TradeOps.Domain.Enums;
using TradeOps.Domain.Oms;

namespace TradeOps.Application.Oms;

public sealed record DeribitFixQualificationQuoteV1(AccountKey Scope,string Instrument,DateTimeOffset ReceivedUtc,DateTimeOffset VenueUtc,
    decimal ContractUsd,decimal AmountStep,decimal PriceStep,decimal Bid,decimal Ask,decimal MinPrice,decimal MaxPrice);
public interface IDeribitFixQualificationMarketV1
{Task<DeribitFixQualificationQuoteV1> ReadAsync(CancellationToken ct);}
public interface IOmsFixQualificationLeaseV1:IAsyncDisposable
{
    Task<int> CheckBudgetAsync(OmsRecoveredAccountStateV1 current,CancellationToken ct);
    Task CheckAsync(CancellationToken ct);
}
public sealed record DeribitFixQualificationResultV1(string Status,bool Placed,bool Amended,bool Cancelled,
    bool TerminalRecovered,bool? UnexpectedFill,bool OwnOrderMayRemain,bool PositionsPreserved,int StagePlacementsUsedUpperBound);

/// <summary>Owner-launched scenario only. All three economic actions enter the real B5 gateway.
/// No direct FIX socket, forced fill, flatten, blind resend, migration or policy provisioning.</summary>
public sealed class DeribitFixQualificationScenarioV1(IOmsOrderIntentGateway gateway,IDeribitFixQualificationMarketV1 market,
    Func<CancellationToken,Task<OmsRecoveredAccountStateV1>> current,IOmsFixQualificationLeaseV1 lease,TimeProvider? time=null)
{
    public const string Provenance="owner-fix-eth-qualification-v1";
    public const int MaximumPlacements=3;
    public async Task<DeribitFixQualificationResultV1> RunAsync(CancellationToken ct)
    {
        var clock=time??TimeProvider.System;var placed=false;var amended=false;var cancelled=false;var terminal=false;
        bool? fill=null;var mayRemain=false;var preserved=false;var spent=0;
        try
        {
            var baseline=await current(ct);var q=await market.ReadAsync(ct);var price=Price(q,clock.GetUtcNow());
            if(q.Scope!=baseline.Scope||baseline.Orders.Any(x=>x.State.NativeState is "open" or "triggered" or "untriggered"))
                return Result("FIX_QUALIFICATION_REFUSED");
            spent=await lease.CheckBudgetAsync(baseline,ct);if(spent>=MaximumPlacements)return Result("FIX_QUALIFICATION_BUDGET_EXHAUSTED");
            var id=Guid.NewGuid();var command=new OmsJournalCommandRequest(q.Scope,new(id),new(Guid.NewGuid()),new(Guid.NewGuid()),new(0),
                new(OmsJournalCommandKind.Place,q.Instrument,OrderSide.Buy,OrderType.Limit,1,OmsQuantityUnit.QuoteAsset,price,null,"GTC",false,true,
                    Provenance,"owner-fix-qualification", "actual-b5-admission-required",clock.GetUtcNow(),"fix-q-"+id.ToString("N")));
            await lease.CheckAsync(ct);mayRemain=true;spent++;
            var response=await gateway.SubmitAsync(command,ct);
            if(response.Kind!=OmsExecutionResultKind.Acknowledged)return Result("FIX_QUALIFICATION_STOPPED_UNKNOWN");placed=true;
            var open=await current(ct);var order=Own(open,command);fill=HasFill(open,command.LocalOrderId);
            if(fill==true)return Result("FIX_QUALIFICATION_STOPPED_FILL");
            if(order.State.NativeState!="open"||order.LastReport.Price!=price||!SamePositions(baseline,open))return Result("FIX_QUALIFICATION_STOPPED_EVIDENCE");
            q=await market.ReadAsync(ct);var revised=Price(q,clock.GetUtcNow(),price);
            if(q.Scope!=command.Scope)return Result("FIX_QUALIFICATION_STOPPED_EVIDENCE");
            var amend=command with{CommandId=new(Guid.NewGuid()),ExpectedAggregateVersion=new(1),Payload=command.Payload with{
                Kind=OmsJournalCommandKind.Replace,LimitPrice=revised,TargetExchangeOrderId=order.State.ExchangeOrderId,RequestedAtUtc=clock.GetUtcNow()}};
            await lease.CheckAsync(ct);fill=null;response=await gateway.SubmitAsync(amend,ct);
            if(response.Kind!=OmsExecutionResultKind.Acknowledged)return Result("FIX_QUALIFICATION_STOPPED_UNKNOWN");amended=true;
            var moved=await current(ct);order=Own(moved,command);fill=HasFill(moved,command.LocalOrderId);
            if(fill==true)return Result("FIX_QUALIFICATION_STOPPED_FILL");
            if(order.State.NativeState!="open"||order.LastReport.Price!=revised||!SamePositions(baseline,moved))return Result("FIX_QUALIFICATION_STOPPED_EVIDENCE");
            var cancel=amend with{CommandId=new(Guid.NewGuid()),ExpectedAggregateVersion=new(2),Payload=amend.Payload with{
                Kind=OmsJournalCommandKind.Cancel,RequestedAtUtc=clock.GetUtcNow()}};
            await lease.CheckAsync(ct);fill=null;response=await gateway.SubmitAsync(cancel,ct);
            if(response.Kind!=OmsExecutionResultKind.Acknowledged)return Result("FIX_QUALIFICATION_STOPPED_UNKNOWN");cancelled=true;
            var final=await current(ct);order=Own(final,command);fill=HasFill(final,command.LocalOrderId);
            if(fill==true)return Result("FIX_QUALIFICATION_STOPPED_FILL");
            terminal=order.State.NativeState=="cancelled"&&order.LastReport.OrderState=="cancelled"&&order.State.KnownFilledNativeAmount==0;
            preserved=SamePositions(baseline,final);mayRemain=!terminal;
            return Result(terminal&&preserved?"FIX_QUALIFICATION_SCENARIO_PASS":"FIX_QUALIFICATION_STOPPED_EVIDENCE");
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){return Result("FIX_QUALIFICATION_STOPPED_TIMEOUT");}
        catch{return Result("FIX_QUALIFICATION_STOPPED_EVIDENCE");}
        DeribitFixQualificationResultV1 Result(string status)=>new(status,placed,amended,cancelled,terminal,fill,mayRemain,preserved,spent);
    }
    public static decimal Price(DeribitFixQualificationQuoteV1 q,DateTimeOffset now,decimal? previous=null)
    {
        if(q.Instrument!="ETH-PERPETUAL"||q.ContractUsd!=1||q.AmountStep<=0||1%q.AmountStep!=0||q.PriceStep<=0||q.Bid<=0||q.Ask<=q.Bid||
            q.MinPrice<=0||q.MaxPrice<q.MinPrice||q.ReceivedUtc.Offset!=TimeSpan.Zero||q.VenueUtc.Offset!=TimeSpan.Zero||now<q.ReceivedUtc||
            now-q.ReceivedUtc>TimeSpan.FromSeconds(5)||q.ReceivedUtc-q.VenueUtc>TimeSpan.FromSeconds(5)||q.VenueUtc>q.ReceivedUtc.AddSeconds(2))
            throw new InvalidOperationException("Unqualified ETH quote or units.");
        var price=decimal.Floor(q.Bid*.90m/q.PriceStep)*q.PriceStep;
        if(previous is{} old)price=Math.Min(price,old-q.PriceStep);
        price=decimal.Floor(price/q.PriceStep)*q.PriceStep;
        // This scenario sends buys. Deribit's lower future band clamps sells, not passive buys.
        if(price<=0||price>q.MaxPrice||price>q.Bid*.90m||price>=q.Ask||price==previous)
            throw new InvalidOperationException("No price satisfies distance and venue limits.");
        return price;
    }
    private static DeribitRecoveredNativeOrder Own(OmsRecoveredAccountStateV1 state,OmsJournalCommandRequest c)
    {var row=state.Orders.Single(x=>x.LocalOrderId==c.LocalOrderId);if(row.State.NativeInstrument!=c.Payload.Instrument||row.State.NativeAmount!=1||row.State.Side!=OrderSide.Buy)throw new InvalidOperationException("Own order proof mismatch.");return row;}
    private static bool HasFill(OmsRecoveredAccountStateV1 s,OmsLocalOrderId id)=>s.Orders.Any(x=>x.LocalOrderId==id&&x.State.KnownFilledNativeAmount is >0)||
        s.Executions.Any(x=>s.Orders.Any(o=>o.LocalOrderId==id&&o.State.ExchangeOrderId==x.ExchangeOrderId&&o.State.Currency==x.Currency));
    private static bool SamePositions(OmsRecoveredAccountStateV1 a,OmsRecoveredAccountStateV1 b)=>
        // Inverse futures' base equivalent changes with mark price. Compare native position
        // quantity and units, retaining complete B4 fill reconciliation as the independent gate.
        OmsRecoveryRunCodec.Encode(a.Positions.OrderBy(x=>x.NativeInstrument,StringComparer.Ordinal).Select(x=>x with{BaseSize=null}).ToArray()).Sha256.SequenceEqual(
            OmsRecoveryRunCodec.Encode(b.Positions.OrderBy(x=>x.NativeInstrument,StringComparer.Ordinal).Select(x=>x with{BaseSize=null}).ToArray()).Sha256);
}
