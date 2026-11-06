using TradeOps.Application.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;
using Live=TradeOps.UnitTests.DeribitRecoveryPublicationPostgresTests.Live;
using Inputs=TradeOps.UnitTests.OmsRiskAdmissionPostgresTests.Inputs;

namespace TradeOps.UnitTests;
public sealed class OmsRiskSettlementPostgresTests
{
    [Theory]
    [InlineData("filled",1)] [InlineData("cancelled",1)] [InlineData("cancelled",0)]
    public async Task ProvenTerminalBaselineRetiresExactlyOnceAndPartialFillIsNotCountedTwice(string state,int filled)
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();await using var live=await Live.Open(db);
        var recovery=await OmsExecutionGatewayPostgresTests.Prepare(live);var risk=new OmsPostgresRiskAdmission(db.Source,new Inputs());
        var c=OmsExecutionGatewayPostgresTests.Command(); if(state=="cancelled"&&filled==1)c=c with{Payload=c.Payload with{Quantity=2}};
        Assert.Equal(OmsRiskAdmissionResult.Reserved,await risk.AdmitAsync(c,recovery.Current!.Publication,recovery.Current.Qualified));
        var journal=new OmsPostgresJournal(db.Source);await journal.MarkDispatchStartedAsync(Scope(),c.CommandId);
        var receipt=DeribitCommandReceiptFixtures.Receipt(c,sent:DateTimeOffset.UtcNow);
        Assert.Equal(DeribitCommandReceiptWriteResult.Inserted,await new OmsPostgresDeribitCommandReceiptJournal(db.Source).RecordAsync(receipt));
        live.NativeReport=receipt.Order with{OrderState=state,FilledNativeAmount=filled,VenueTimestampUtc=receipt.Order.VenueTimestampUtc.AddMilliseconds(2)};
        live.FullFill=filled>0;live.DuplicateFill=true;
        var next=await OmsExecutionGatewayPostgresTests.Prepare(live);
        Assert.Equal(OmsRiskAdmissionResult.Reserved,await risk.AdmitAsync(OmsExecutionGatewayPostgresTests.Command(),next.Current!.Publication,next.Current.Qualified));
        Assert.Equal(1,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_settlements_v1"));
        Assert.Equal(OmsRiskAdmissionResult.Reserved,await risk.AdmitAsync(OmsExecutionGatewayPostgresTests.Command(),next.Current.Publication,next.Current.Qualified));
        Assert.Equal(OmsRiskAdmissionResult.Denied,await risk.AdmitAsync(OmsExecutionGatewayPostgresTests.Command(),next.Current.Publication,next.Current.Qualified));
        Assert.Equal(1,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_settlements_v1"));
        Assert.Equal(3,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_reservations_v1"));
    }
    [Fact]
    public async Task DirectCancelAckWithoutNewCurrentRecoveryCannotReleasePending()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();await using var live=await Live.Open(db);
        var r=await OmsExecutionGatewayPostgresTests.Prepare(live);var c=OmsExecutionGatewayPostgresTests.Command();
        var risk=new OmsPostgresRiskAdmission(db.Source,new Inputs());await risk.AdmitAsync(c,r.Current!.Publication,r.Current.Qualified);
        var journal=new OmsPostgresJournal(db.Source);await journal.MarkDispatchStartedAsync(Scope(),c.CommandId);
        var receipts=new OmsPostgresDeribitCommandReceiptJournal(db.Source);await receipts.RecordAsync(DeribitCommandReceiptFixtures.Receipt(c,sent:DateTimeOffset.UtcNow));
        var cancel=DeribitCommandReceiptFixtures.Cancel(c);await journal.PersistAsync(cancel);await journal.MarkDispatchStartedAsync(Scope(),cancel.CommandId);
        await receipts.RecordAsync(DeribitCommandReceiptFixtures.Receipt(cancel,rpcId:2,sent:DateTimeOffset.UtcNow));
        Assert.Equal(OmsRiskAdmissionResult.StaleBaseline,await risk.AdmitAsync(OmsExecutionGatewayPostgresTests.Command(),r.Current.Publication,r.Current.Qualified));
        Assert.Equal(0,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_settlements_v1"));
        Assert.Equal(1,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_reservations_v1"));
    }
}
