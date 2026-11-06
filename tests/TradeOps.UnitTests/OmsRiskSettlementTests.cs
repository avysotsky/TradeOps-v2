using TradeOps.Application.Oms;
using Xunit;
namespace TradeOps.UnitTests;
public sealed class OmsRiskSettlementTests
{
    [Theory]
    [InlineData("filled",1)] [InlineData("cancelled",0)] [InlineData("cancelled",0.5)] [InlineData("rejected",0)]
    public void OnlyTerminalOrderWithExactCompleteFillsRetiresRisk(string state,decimal filled)
    {
        var c=DeribitCommandReceiptFixtures.Command();var receipt=DeribitCommandReceiptFixtures.Receipt(c);
        var at=receipt.ReceivedAtUtc.AddSeconds(1);var report=receipt.Order with { OrderState=state,FilledNativeAmount=filled,VenueTimestampUtc=at };
        var order=new DeribitRecoveredNativeOrder(new("ETH",c.Payload.Instrument,receipt.Order.ExchangeOrderId,state,c.Payload.Side,1,OmsPrivateAmountUnit.Usd,"USD",filled,true,true),report,c.LocalOrderId);
        var fills=filled==0 ? Array.Empty<OmsPrivateVenueFacts>() : new[]{report with { Kind=OmsJournalEventKind.Execution,NativeAmount=filled,OrderState=null,FilledNativeAmount=null,ExchangeTradeId="fill-1" }};
        var b=DeribitRiskInputsTests.Baseline() with { Scope=c.Scope,Orders=[order],Executions=fills,NativeThroughUtc=at };
        Assert.True(OmsRiskSettlementV1.CanRetire(b,receipt));
        Assert.False(OmsRiskSettlementV1.CanRetire(b with { Orders=[order with { State=order.State with { NativeState="open" } }] },receipt));
        Assert.False(OmsRiskSettlementV1.CanRetire(b with { Orders=[] },receipt));
        Assert.False(OmsRiskSettlementV1.CanRetire(b with { Orders=[order with { LocalOrderId=new(Guid.NewGuid()) }] },receipt));
        if(filled>0)
        {
            Assert.False(OmsRiskSettlementV1.CanRetire(b with { Executions=[] },receipt));
            Assert.False(OmsRiskSettlementV1.CanRetire(b with { Executions=[fills[0],fills[0]] },receipt));
            Assert.False(OmsRiskSettlementV1.CanRetire(b with { Executions=[fills[0] with { NativeAmount=filled+.1m }] },receipt));
        }
    }
    [Fact]
    public void ReceiptOrCancelAckAloneDoesNotRetireRisk()
    {
        var receipt=DeribitCommandReceiptFixtures.Receipt(DeribitCommandReceiptFixtures.Command());
        Assert.False(OmsRiskSettlementV1.CanRetire(DeribitRiskInputsTests.Baseline(),receipt));
    }
    [Fact]
    public void SeededPartialFillPartitionsAndReplaysCannotRetireMissingOrDuplicatedExecution()
    {
        var random=new Random(617392);
        for(var sample=0;sample<100;sample++)
        {
            var c=DeribitCommandReceiptFixtures.Command();c=c with{Payload=c.Payload with{Quantity=100}};
            var receipt=DeribitCommandReceiptFixtures.Receipt(c);var at=receipt.ReceivedAtUtc.AddSeconds(1);
            var amounts=Enumerable.Range(0,random.Next(1,12)).Select(_=>(decimal)random.Next(1,8)).ToArray();var filled=amounts.Sum();
            var report=receipt.Order with{OrderState="cancelled",FilledNativeAmount=filled,VenueTimestampUtc=at};
            var fills=amounts.Select((x,i)=>report with{Kind=OmsJournalEventKind.Execution,NativeAmount=x,OrderState=null,FilledNativeAmount=null,ExchangeTradeId="fill-"+i}).OrderBy(_=>random.Next()).ToArray();
            var order=new DeribitRecoveredNativeOrder(new("ETH",c.Payload.Instrument,receipt.Order.ExchangeOrderId,"cancelled",c.Payload.Side,100,OmsPrivateAmountUnit.Usd,"USD",filled,true,true),report,c.LocalOrderId);
            var b=DeribitRiskInputsTests.Baseline() with{Scope=c.Scope,NativeThroughUtc=at,Orders=[order],Executions=fills};
            Assert.True(OmsRiskSettlementV1.CanRetire(b,receipt));
            Assert.False(OmsRiskSettlementV1.CanRetire(b with{Executions=fills.Skip(1).ToArray()},receipt));
            Assert.False(OmsRiskSettlementV1.CanRetire(b with{Executions=fills.Append(fills[0]).ToArray()},receipt));
        }
    }
}
