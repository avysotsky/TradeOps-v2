using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit.Fix;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitFixPriceAmendTests
{
    internal static (DeribitFixCommandBindingV1 Binding,DeribitFixExecutionReportV1 Report,DeribitNativeCommandView Parent) Evidence()
    {
        var (place,ack)=DeribitFixEvidenceTests.Evidence();var now=DateTimeOffset.UtcNow.AddMilliseconds(10);
        var command=place.Command with{CommandId=new(Guid.NewGuid()),ExpectedAggregateVersion=new(1),Payload=place.Command.Payload with{
            Kind=OmsJournalCommandKind.Replace,LimitPrice=place.Command.Payload.LimitPrice-1,TargetExchangeOrderId=ack.NativeOrderId,RequestedAtUtc=now}};
        var binding=new DeribitFixCommandBindingV1(2,command,place.Session,ack.ConnectionEpoch,now,"ETH",1,DeribitFixCommandBindingV1.ClientId(command),ack);
        var report=ack with{Price=command.Payload.LimitPrice,ChangeId="change-2",TransactTimeUtc=now.AddMilliseconds(1),ReceivedAtUtc=now.AddMilliseconds(2)};
        return(binding,report,place.Match(ack)!);
    }
    [Fact]
    public void NativeGKeepsQuantityFlagsAndNativeIdentityAndCancelUsesTheAmendedParent()
    {
        var (b,r,parent)=Evidence();Assert.True(DeribitFixPriceAmendPolicyV2.Terms(b.Command,parent));
        var m=DeribitFixCommandEncoderV1.Encode(b.Command,b.Session,DeribitFixCommandTests.Metadata() with{ObservedAtUtc=b.PreparedAtUtc},b.PreparedAtUtc,new(parent.Command,b.Parent!));
        Assert.Equal("G",m.Header.GetString(35));Assert.Equal(b.Parent!.ServerAssignedClOrdId,m.GetString(41));Assert.False(m.IsSetField(11));
        Assert.Equal(parent.Command.Payload.Quantity,m.GetDecimal(38));Assert.Equal(0,m.GetInt(854));Assert.Equal(b.Command.Payload.LimitPrice,m.GetDecimal(44));Assert.Equal("6A",m.GetString(18));
        var view=b.Match(r)!;Assert.NotNull(view);view.Validate();
        var cancel=DeribitCommandReceiptFixtures.Cancel(b.Command,r.NativeOrderId!) with{ExpectedAggregateVersion=new(2)};
        var f=DeribitFixCommandEncoderV1.Encode(cancel,b.Session,DeribitFixCommandTests.Metadata() with{ObservedAtUtc=b.PreparedAtUtc},b.PreparedAtUtc.AddMilliseconds(5),new(b.Command,r));
        Assert.Equal("F",f.Header.GetString(35));Assert.Equal(r.ServerAssignedClOrdId,f.GetString(41));Assert.False(f.IsSetField(11));
        var archived=OmsRecoveryRunCodec.Encode(b);Assert.Equal(b,OmsRecoveryRunCodec.Decode<DeribitFixCommandBindingV1>(archived.Json,archived.Sha256));
        Assert.Throws<ArgumentException>(()=>(b with{SchemaVersion=1}).Validate());
    }
    [Theory]
    [InlineData("quantity")][InlineData("side")][InlineData("post-only")][InlineData("reduce-only")][InlineData("label")]
    [InlineData("native-id")][InlineData("version")][InlineData("same-price")][InlineData("foreign-order")][InlineData("raw-id")]
    public void PriceOnlyPolicyRejectsAnyOtherMutation(string problem)
    {
        var (b,_,parent)=Evidence();var c=b.Command;
        c=problem switch{
            "quantity"=>c with{Payload=c.Payload with{Quantity=2}},"side"=>c with{Payload=c.Payload with{Side=TradeOps.Domain.Enums.OrderSide.Sell}},
            "post-only"=>c with{Payload=c.Payload with{PostOnly=false}},"reduce-only"=>c with{Payload=c.Payload with{ReduceOnly=true}},
            "label"=>c with{Payload=c.Payload with{TargetClientOrderId="changed-label"}},"native-id"=>c with{Payload=c.Payload with{TargetExchangeOrderId="foreign"}},
            "version"=>c with{ExpectedAggregateVersion=new(2)},"same-price"=>c with{Payload=c.Payload with{LimitPrice=parent.Command.Payload.LimitPrice}},
            "foreign-order"=>c with{LocalOrderId=new(Guid.NewGuid())},_=>c with{Payload=c.Payload with{FixClOrdId="raw"}}};
        Assert.False(DeribitFixPriceAmendPolicyV2.Terms(c,parent));
    }
    [Theory]
    [InlineData("old-change")][InlineData("no-change")][InlineData("old-time")][InlineData("filled")][InlineData("rejected")]
    [InlineData("server-id-as-client")][InlineData("other-native")][InlineData("other-connection")][InlineData("old-price")]
    public void NativeAckNeedsANewZeroFillChangeAtTheExactApprovedPrice(string problem)
    {
        var (b,r,_)=Evidence();r=problem switch{
            "old-change"=>r with{ChangeId=b.Parent!.ChangeId},"no-change"=>r with{ChangeId=null},"old-time"=>r with{TransactTimeUtc=b.Parent!.TransactTimeUtc},
            "filled"=>r with{OrderStatus="1",CumQty=.5m,LeavesQty=.5m},"rejected"=>r with{OrderStatus="8",LeavesQty=0,RejectReason=100},
            "server-id-as-client"=>r with{OriginalClientClOrdId=r.ServerAssignedClOrdId},"other-native"=>r with{NativeOrderId="foreign"},
            "other-connection"=>r with{ConnectionEpoch=Guid.NewGuid()},_=>r with{Price=b.Parent!.Price}};
        Assert.Null(b.Match(r));
    }
    [Fact]
    public void RecoveryAcceptsOnlyTheProvenPriceEffectiveAtTheNativeObservationTime()
    {
        var (b,r,parent)=Evidence();var changed=b.Match(r)!;var views=new[]{parent,changed};
        Assert.True(DeribitNativePriceHistoryV2.Matches(views,parent,parent.Order));
        Assert.True(DeribitNativePriceHistoryV2.Matches(views,parent,changed.Order));
        Assert.False(DeribitNativePriceHistoryV2.Matches(views,parent,parent.Order with{VenueTimestampUtc=changed.Order.VenueTimestampUtc.AddMilliseconds(1)}));
        Assert.False(DeribitNativePriceHistoryV2.Matches(new[]{parent},parent,changed.Order));
    }
    [Theory]
    [InlineData("partial")][InlineData("filled")][InlineData("old-price")][InlineData("no-proven-amend")]
    public void LaterNativeFillsRequireAnAlreadyProvenAmendmentAndNeverBecomeItsAck(string mode)
    {
        var (b,r,parent)=Evidence();var changed=b.Match(r)!;
        var follow=r with{TransactTimeUtc=r.TransactTimeUtc.AddMilliseconds(10),ReceivedAtUtc=r.ReceivedAtUtc.AddMilliseconds(10),ChangeId="fill-change",
            OrderStatus=mode=="filled"?"2":"1",CumQty=mode=="filled"?1:.5m,LeavesQty=mode=="filled"?0:.5m};
        if(mode=="old-price")follow=follow with{Price=parent.Order.Price};
        Assert.Null(b.Match(follow));
        var views=mode=="no-proven-amend"?new[]{parent}:new[]{parent,changed};
        if(mode is "partial" or "filled")Assert.Equal(changed,DeribitNativePriceHistoryV2.FollowUp(views,follow));
        else Assert.Null(DeribitNativePriceHistoryV2.FollowUp(views,follow));
    }
}
