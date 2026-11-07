using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitFixQualificationScenarioTests
{
    private sealed class Clock:TimeProvider { public override DateTimeOffset GetUtcNow()=>DeribitRiskInputsTests.Now; }
    private sealed class Rig:IOmsOrderIntentGateway,IDeribitFixQualificationMarketV1,IOmsFixQualificationLeaseV1
    {
        internal readonly List<OmsJournalCommandRequest> Commands=[];
        internal OmsRecoveredAccountStateV1 State=DeribitRiskInputsTests.Baseline();
        internal string Mode="normal";internal int Budget,Reads,Checks;internal bool Disposed;
        internal DeribitFixQualificationQuoteV1 Quote=>new(State.Scope,"ETH-PERPETUAL",DeribitRiskInputsTests.Now,DeribitRiskInputsTests.Now,1,1,.5m,2000,2001,1000,3000);
        public Task<DeribitFixQualificationQuoteV1> ReadAsync(CancellationToken ct)
        {Reads++;return Task.FromResult(Mode=="stale"||Mode=="stale-after-place"&&Reads>1?Quote with{VenueUtc=Quote.VenueUtc.AddSeconds(-6)}:Quote);}
        public Task<int> CheckBudgetAsync(OmsRecoveredAccountStateV1 current,CancellationToken ct)=>Task.FromResult(Budget);
        public Task CheckAsync(CancellationToken ct)
        {Checks++;if(Mode=="lease-lost"&&Checks==2)throw new IOException();return Task.CompletedTask;}
        public ValueTask DisposeAsync(){Disposed=true;return ValueTask.CompletedTask;}
        public Task<OmsExecutionResult> SubmitAsync(OmsJournalCommandRequest c,CancellationToken ct=default)
        {
            Commands.Add(c);var step=Commands.Count;
            if(Mode=="unknown-"+step)return Task.FromResult(new OmsExecutionResult(OmsExecutionResultKind.Unknown,null,"synthetic"));
            var filled=Mode=="fill-"+step?.5m:0m;var status=c.Payload.Kind==OmsJournalCommandKind.Cancel?"cancelled":"open";
            var report=DeribitCommandReceiptFixtures.Receipt(c with{Payload=c.Payload with{Kind=OmsJournalCommandKind.Place,TargetExchangeOrderId=null}}).Order with{
                ExchangeOrderId="synthetic-order",OrderState=status,Price=c.Payload.LimitPrice,FilledNativeAmount=filled};
            State=State with{Orders=[new(new("ETH",c.Payload.Instrument,"synthetic-order",status,c.Payload.Side,1,OmsPrivateAmountUnit.Usd,"USD",filled,true,true),report,c.LocalOrderId)]};
            if(Mode=="position-drift")State=State with{Positions=[]};
            if(Mode=="mark-move")State=State with{Positions=State.Positions.Select(x=>x with{BaseSize=x.BaseSize*1.001m}).ToArray()};
            return Task.FromResult(new OmsExecutionResult(OmsExecutionResultKind.Acknowledged,null,"synthetic"));
        }
        internal Task<DeribitFixQualificationResultV1> Run()=>new DeribitFixQualificationScenarioV1(this,this,_=>Task.FromResult(State),this,new Clock()).RunAsync(default);
    }
    [Fact]
    public async Task SuccessfulScenarioUsesOnePlacementNativePriceOnlyAmendAndOwnCancel()
    {
        var rig=new Rig();var r=await rig.Run();Assert.Equal("FIX_QUALIFICATION_SCENARIO_PASS",r.Status);
        Assert.True(r.Placed&&r.Amended&&r.Cancelled&&r.TerminalRecovered&&r.PositionsPreserved);Assert.False(r.UnexpectedFill);Assert.False(r.OwnOrderMayRemain);
        Assert.Equal(new[]{OmsJournalCommandKind.Place,OmsJournalCommandKind.Replace,OmsJournalCommandKind.Cancel},rig.Commands.Select(x=>x.Payload.Kind));
        Assert.Equal(new long[]{0,1,2},rig.Commands.Select(x=>x.ExpectedAggregateVersion.Value));
        Assert.All(rig.Commands,x=>{Assert.Equal(1,x.Payload.Quantity);Assert.True(x.Payload.PostOnly);Assert.False(x.Payload.ReduceOnly);Assert.Equal(rig.Commands[0].LocalOrderId,x.LocalOrderId);});
        Assert.True(rig.Commands[1].Payload.LimitPrice<rig.Commands[0].Payload.LimitPrice);Assert.Equal(1,r.StagePlacementsUsedUpperBound);
    }
    [Theory]
    [InlineData("unknown-1",1,"UNKNOWN")][InlineData("unknown-2",2,"UNKNOWN")][InlineData("unknown-3",3,"UNKNOWN")]
    [InlineData("fill-1",1,"FILL")][InlineData("fill-2",2,"FILL")][InlineData("fill-3",3,"FILL")]
    [InlineData("position-drift",1,"EVIDENCE")][InlineData("stale-after-place",1,"EVIDENCE")][InlineData("lease-lost",1,"EVIDENCE")]
    public async Task UncertainOrUnsafeOutcomeStopsWithoutRetryOrFlatten(string mode,int commands,string status)
    {
        var rig=new Rig{Mode=mode};var r=await rig.Run();Assert.Equal("FIX_QUALIFICATION_STOPPED_"+status,r.Status);
        Assert.Equal(commands,rig.Commands.Count);Assert.True(r.OwnOrderMayRemain);Assert.False(r.TerminalRecovered);
        if(status=="UNKNOWN")Assert.Null(r.UnexpectedFill);if(status=="FILL")Assert.True(r.UnexpectedFill);
    }
    [Theory]
    [InlineData("normal")][InlineData("mark-move")]
    public async Task NativePositionSizeRemainsTheInvariantWhenInverseBaseEquivalentMoves(string mode)
    {var rig=new Rig{Mode=mode};var r=await rig.Run();Assert.Equal("FIX_QUALIFICATION_SCENARIO_PASS",r.Status);Assert.True(r.PositionsPreserved);}
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task AnyExistingActiveOrderRefusesNewPlacementWithoutTouchingIt(bool external)
    {
        var rig=new Rig();var c=DeribitFixCommandTests.Command();var report=DeribitCommandReceiptFixtures.Receipt(c).Order;
        rig.State=rig.State with{Orders=[new(new("ETH",c.Payload.Instrument,report.ExchangeOrderId,"open",c.Payload.Side,1,OmsPrivateAmountUnit.Usd,"USD",0,true,true),report,external?null:c.LocalOrderId)]};
        var r=await rig.Run();Assert.Equal("FIX_QUALIFICATION_REFUSED",r.Status);Assert.Empty(rig.Commands);Assert.False(r.OwnOrderMayRemain);
    }
    [Theory]
    [InlineData(3,"normal","FIX_QUALIFICATION_BUDGET_EXHAUSTED")][InlineData(0,"stale","FIX_QUALIFICATION_STOPPED_EVIDENCE")]
    public async Task PreflightCannotSpendAnUnavailableBudgetOrUseAStaleQuote(int budget,string mode,string status)
    {var rig=new Rig{Budget=budget,Mode=mode};Assert.Equal(status,(await rig.Run()).Status);Assert.Empty(rig.Commands);}
    [Theory]
    [InlineData("contract")][InlineData("amount")][InlineData("tick")][InlineData("band")][InlineData("crossed")][InlineData("future")]
    public void InvalidQuotesNeverRelaxNativeSizeDistanceOrVenueBand(string problem)
    {
        var q=new Rig().Quote;q=problem switch{"contract"=>q with{ContractUsd=10},"amount"=>q with{AmountStep=3},"tick"=>q with{PriceStep=0},
            "band"=>q with{MinPrice=1900},"crossed"=>q with{Ask=q.Bid},_=>q with{VenueUtc=q.ReceivedUtc.AddSeconds(3)}};
        Assert.Throws<InvalidOperationException>(()=>DeribitFixQualificationScenarioV1.Price(q,DeribitRiskInputsTests.Now));
    }
}
