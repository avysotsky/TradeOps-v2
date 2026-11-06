using QuickFix;
using TradeOps.Connectors.Deribit.Fix;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitFixNativeReportTests
{
    private static Message Order(DeribitQuickFixSessionTests.Rig r)=>DeribitQuickFixSessionTests.Parse(r.Peer("8",2,
        (37,"ETH-venue-order-77"),(11,"native-generated-A"),(41,"client-intent-0001"),(17,"native-exec-17"),(527,"order-change-8"),
        (55,"ETH-PERPETUAL"),(54,"1"),(39,"1"),(150,"F"),(60,DateTime.UtcNow.ToString("yyyyMMdd-HH:mm:ss.fff",System.Globalization.CultureInfo.InvariantCulture)),
        (38,"10"),(152,"10"),(14,"4"),(151,"6"),(854,"1"),(231,"1"),(40,"2"),(44,"1000"),(59,"1"),(18,"6AE"),
        (103,"0"),(32,"4"),(31,"1000"),(880,"native-match-42"),(100010,"intent-label")));
    [Fact]
    public void ServerClientOrderExecutionAndTradeIdsRemainDistinctAndPartialFillUnitsAreExplicit()
    {
        using var r=new DeribitQuickFixSessionTests.Rig();var report=DeribitFixReportParserV1.Execution(Order(r),r.Key,Guid.NewGuid(),DateTimeOffset.UtcNow);
        Assert.Equal("ETH-venue-order-77",report.NativeOrderId);Assert.Equal("native-generated-A",report.ServerAssignedClOrdId);
        Assert.Equal("client-intent-0001",report.OriginalClientClOrdId);Assert.Equal("native-exec-17",report.ExecId);Assert.Equal("order-change-8",report.ChangeId);
        Assert.Equal("native-match-42",report.TradeMatchId);Assert.Equal(4,report.CumQty);Assert.Equal(6,report.LeavesQty);
        Assert.True(report.HasUnambiguousUsdAmounts(1));Assert.False(report.HasUnambiguousUsdAmounts(10));
    }
    [Fact]
    public void ReplayAndConnectionHeadersDoNotCreateAnotherEconomicEventIdentity()
    {
        using var r=new DeribitQuickFixSessionTests.Rig();var first=DeribitFixReportParserV1.Execution(Order(r),r.Key,Guid.NewGuid(),DateTimeOffset.UtcNow);
        var replay=first with{Session=r.Key with{SenderCompId="another-connection"},ConnectionEpoch=Guid.NewGuid(),Sequence=100,PossDup=true,ExecId="connection-local-exec",SendingTimeUtc=DateTimeOffset.UtcNow,ReceivedAtUtc=DateTimeOffset.UtcNow};
        replay.Validate();Assert.Equal(first.SemanticSha256(),replay.SemanticSha256());
        Assert.NotEqual(first.SemanticSha256(),(first with{LastQty=3}).SemanticSha256());
    }
    [Theory]
    [InlineData("missing-cash")][InlineData("multiplier-ten")][InlineData("missing-trade-id")][InlineData("exponent")]
    [InlineData("cash-mismatch")][InlineData("wrong-session")][InlineData("fills-group")][InlineData("overfilled")]
    public void AmbiguousUnitsOrMalformedNativeEvidenceNeverBecomeUsdApproval(string variant)
    {
        using var r=new DeribitQuickFixSessionTests.Rig();var m=Order(r);
        if(variant=="missing-cash")m.RemoveField(152);
        if(variant=="multiplier-ten")m.SetField(new QuickFix.Fields.StringField(231,"10"));
        if(variant=="missing-trade-id")m.RemoveField(880);
        if(variant=="exponent")m.SetField(new QuickFix.Fields.StringField(14,"4e-100"));
        if(variant=="cash-mismatch")m.SetField(new QuickFix.Fields.StringField(152,"100"));
        if(variant=="wrong-session")m.Header.SetField(new QuickFix.Fields.TargetCompID("someone-else"));
        if(variant=="fills-group")m.SetField(new QuickFix.Fields.StringField(1362,"1"));
        if(variant=="overfilled")m.SetField(new QuickFix.Fields.StringField(14,"11"));
        if(variant is "missing-cash" or "multiplier-ten" or "overfilled")
            Assert.False(DeribitFixReportParserV1.Execution(m,r.Key,Guid.NewGuid(),DateTimeOffset.UtcNow).HasUnambiguousUsdAmounts(1));
        else if(variant is "missing-trade-id" or "cash-mismatch")
            Assert.Throws<ArgumentException>(()=>DeribitFixReportParserV1.Execution(m,r.Key,Guid.NewGuid(),DateTimeOffset.UtcNow));
        else Assert.Throws<InvalidOperationException>(()=>DeribitFixReportParserV1.Execution(m,r.Key,Guid.NewGuid(),DateTimeOffset.UtcNow));
    }
    [Fact]
    public void NativeCancelRejectIsPreservedWithoutFabricatingAnOrderCancellation()
    {
        using var r=new DeribitQuickFixSessionTests.Rig();var m=DeribitQuickFixSessionTests.Parse(r.Peer("9",2,
            (37,"ETH-venue-order-77"),(11,"server-order-A"),(41,"original-id"),(39,"1"),(102,"1"),(434,"1"),(55,"ETH-PERPETUAL")));
        var report=DeribitFixReportParserV1.CancelReject(m,r.Key,Guid.NewGuid(),DateTimeOffset.UtcNow);
        Assert.Equal("1",report.OrderStatus);Assert.Equal(1,report.RejectReason);Assert.Equal("1",report.ResponseTo);
    }
}
