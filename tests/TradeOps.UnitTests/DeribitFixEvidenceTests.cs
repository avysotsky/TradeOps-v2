using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit.Fix;
using Xunit;
namespace TradeOps.UnitTests;

public sealed class DeribitFixEvidenceTests
{
    internal static (DeribitFixCommandBindingV1 Binding,DeribitFixExecutionReportV1 Report) Evidence()
    {
        var command=DeribitFixCommandTests.Command();var key=OmsFixSessionStorePostgresTests.Key();
        var at=DateTimeOffset.UtcNow;var report=DeribitFixCommandTests.Ack(command,key);
        return(new(1,command,key,report.ConnectionEpoch,at,"ETH",1,DeribitFixCommandBindingV1.ClientId(command),null),report);
    }
    [Fact]
    public void NativeBindingRoundTripPreservesCommandAndDoesNotBecomeAWsReceiptOrFill()
    {
        var (binding,report)=Evidence();var encoded=OmsRecoveryRunCodec.Encode(binding);
        Assert.Equal(binding,OmsRecoveryRunCodec.Decode<DeribitFixCommandBindingV1>(encoded.Json,encoded.Sha256));
        var view=binding.Match(report)!;Assert.NotNull(view);view.Validate();Assert.Null(view.WsReceipt);
        Assert.Equal(OmsJournalEventKind.Order,view.Order.Kind);Assert.Null(view.Order.ExchangeTradeId);Assert.Null(view.Order.Fee);
        Assert.Throws<ArgumentException>(()=>(view with{Order=view.Order with{NativeAmount=2}}).Validate());
        Assert.Throws<ArgumentException>(()=>(view with{FixReport=null}).Validate());
    }
    [Theory]
    [InlineData("label-only")][InlineData("server-is-client")][InlineData("other-session")][InlineData("no-cash")]
    [InlineData("cancel-leaves")][InlineData("rejected-fill")][InlineData("reject-on-open")][InlineData("contract-ten")][InlineData("amount")][InlineData("before-send")][InlineData("post-only-amended")]
    public void NativeEvidenceCannotBeBackfilledFromALabelOrAmbiguousUnits(string change)
    {
        var (binding,report)=Evidence();report=change switch{
            "label-only"=>report with{OriginalClientClOrdId=null},"server-is-client"=>report with{OriginalClientClOrdId=report.ServerAssignedClOrdId},
            "other-session"=>report with{Session=report.Session with{SenderCompId="other"}},"no-cash"=>report with{CashOrderQty=null},
            "cancel-leaves"=>report with{OrderStatus="4"},"rejected-fill"=>report with{OrderStatus="8",CumQty=.5m,LeavesQty=0},
            "reject-on-open"=>report with{RejectReason=100},"contract-ten"=>report with{ContractMultiplier=10},"amount"=>report with{OrderQty=2,CashOrderQty=2},
            "before-send"=>report with{ReceivedAtUtc=binding.PreparedAtUtc.AddMilliseconds(-1)},_=>report with{ExecutionInstructions="6"}};
        Assert.Null(binding.Match(report));
    }
    [Fact]
    public void CancelBindingRequiresNativePlacementAndNoFakeOrderIsInferredFromAReject()
    {
        var (binding,report)=Evidence();var cancel=DeribitCommandReceiptFixtures.Cancel(binding.Command,report.NativeOrderId!);
        var c=new DeribitFixCommandBindingV1(1,cancel,binding.Session,binding.ConnectionEpoch,DateTimeOffset.UtcNow,"ETH",1,DeribitFixCommandBindingV1.ClientId(cancel),report);
        c.Validate();Assert.Null(c.Match(report));
        Assert.Throws<ArgumentException>(()=>(c with{Parent=null}).Validate());
        Assert.Null(binding.Match(report with{NativeOrderId=null,OrderStatus="8",LeavesQty=0}));
    }
}
