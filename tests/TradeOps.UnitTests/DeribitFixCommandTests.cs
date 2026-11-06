using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit.Fix;
using TradeOps.Domain.Oms;
using Xunit;

namespace TradeOps.UnitTests;
public sealed class DeribitFixCommandTests
{
    internal static OmsJournalCommandRequest Command()=>OmsExecutionGatewayPostgresTests.Command();
    internal static DeribitFixInstrumentV1 Metadata()=>new("ETH-PERPETUAL","future","reversed","ETH","USD",1,1,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddDays(1));
    internal static DeribitFixExecutionReportV1 Ack(OmsJournalCommandRequest command,OmsFixSessionKeyV1 key)
    {
        var p=command.Payload;var now=DateTimeOffset.UtcNow;
        return new(1,key,Guid.NewGuid(),2,false,now,now,now,"native-order-id","server-cl-order-id",DeribitFixCommandEncoderV1.ClientId(command),
            null,"change-1",p.Instrument,p.Side,"0","I",p.Quantity,p.Quantity,0,p.Quantity,1,1,"2",p.LimitPrice,"1",
            (p.PostOnly?"6A":"")+(p.ReduceOnly?"E":""),p.TargetClientOrderId,0,null,null,null);
    }
    [Fact]
    public void OrderUsesNativeUsdUnitsAndExactRiskApprovedTermsWithoutRepricing()
    {
        using var r=new DeribitQuickFixSessionTests.Rig();var c=Command() with{Payload=Command().Payload with{Quantity=3,ReduceOnly=true,TargetClientOrderId="exact-label"}};
        var m=DeribitFixCommandEncoderV1.Encode(c,r.Key,Metadata(),DateTimeOffset.UtcNow);
        Assert.Equal("D",m.Header.GetString(35));Assert.Equal(c.Payload.Quantity,m.GetDecimal(38));Assert.Equal(0,m.GetInt(854));
        Assert.Equal(c.Payload.LimitPrice,m.GetDecimal(44));Assert.Equal("6AE",m.GetString(18));Assert.Equal("1",m.GetString(59));
        Assert.Equal(DeribitFixCommandEncoderV1.ClientId(c),m.GetString(11));Assert.Equal("exact-label",m.GetString(100010));
    }
    [Fact]
    public void CancelTargetsTheServerAssignedClOrdIdWithoutInventingANewClientCancelId()
    {
        using var r=new DeribitQuickFixSessionTests.Rig();var place=Command();var ack=Ack(place,r.Key);var cancel=DeribitCommandReceiptFixtures.Cancel(place,ack.NativeOrderId!);
        var m=DeribitFixCommandEncoderV1.Encode(cancel,r.Key,Metadata(),DateTimeOffset.UtcNow,new(place,ack));
        Assert.Equal("F",m.Header.GetString(35));Assert.Equal("server-cl-order-id",m.GetString(41));Assert.False(m.IsSetField(11));Assert.False(m.IsSetField(37));
    }
    [Theory]
    [InlineData("btc-contract-ten")][InlineData("stale")][InlineData("wrong-product")][InlineData("foreign-parent")]
    [InlineData("wrong-native-id")][InlineData("server-client-swap")][InlineData("replace")][InlineData("changed-amount")]
    public void AmbiguousProductsOrMismatchedCancelTermsCannotReachTheWire(string variant)
    {
        using var r=new DeribitQuickFixSessionTests.Rig();var place=Command();var ack=Ack(place,r.Key);var c=DeribitCommandReceiptFixtures.Cancel(place,ack.NativeOrderId!);var metadata=Metadata();
        if(variant=="btc-contract-ten")metadata=metadata with{ContractUsd=10};
        if(variant=="stale")metadata=metadata with{ObservedAtUtc=DateTimeOffset.UtcNow.AddSeconds(-6)};
        if(variant=="wrong-product")metadata=metadata with{InstrumentType="linear"};
        if(variant=="foreign-parent")ack=ack with{Session=ack.Session with{Scope=TradeOps.UnitTests.Support.DeribitCaptureTestRig.Scope("foreign")}};
        if(variant=="wrong-native-id")ack=ack with{NativeOrderId="another-order"};
        if(variant=="server-client-swap")ack=ack with{OriginalClientClOrdId=ack.ServerAssignedClOrdId};
        if(variant=="replace")c=c with{Payload=c.Payload with{Kind=OmsJournalCommandKind.Replace}};
        if(variant=="changed-amount")c=c with{Payload=c.Payload with{Quantity=c.Payload.Quantity+1}};
        Assert.Throws<InvalidOperationException>(()=>DeribitFixCommandEncoderV1.Encode(c,r.Key,metadata,DateTimeOffset.UtcNow,new(place,ack)));
    }
}
