using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;
using Live=TradeOps.UnitTests.DeribitRecoveryPublicationPostgresTests.Live;
using Inputs=TradeOps.UnitTests.OmsRiskAdmissionPostgresTests.Inputs;

namespace TradeOps.UnitTests;
public sealed class OmsAddressedCancelPostgresTests
{
    internal sealed class Policies:IOmsRiskPolicyReader
    {
        public Task<OmsRiskPolicyDocumentV1?> ReadAsync(AccountKey scope,CancellationToken ct=default)=>Task.FromResult<OmsRiskPolicyDocumentV1?>(DeribitRiskInputsTests.Policy() with{
            Limits=DeribitRiskInputsTests.Policy().Limits with{Scope=scope},ValidFromUtc=DateTimeOffset.UtcNow.AddMinutes(-1),ValidThroughUtc=DateTimeOffset.UtcNow.AddMinutes(1)});
    }
    internal static async Task<(OmsJournalCommandRequest,DeribitJsonRpcCommandReceipt,OmsExecutionGatewayPostgresTests.Recovery)> Seed(OmsPostgresTestDatabase db,Live live)
    {
        var first=await OmsExecutionGatewayPostgresTests.Prepare(live);var c=OmsExecutionGatewayPostgresTests.Command();
        Assert.Equal(OmsRiskAdmissionResult.Reserved,await new OmsPostgresRiskAdmission(db.Source,new Inputs()).AdmitAsync(c,first.Current!.Publication,first.Current.Qualified));
        await new OmsPostgresJournal(db.Source).MarkDispatchStartedAsync(Scope(),c.CommandId);
        var receipt=DeribitCommandReceiptFixtures.Receipt(c,sent:DateTimeOffset.UtcNow);
        Assert.Equal(DeribitCommandReceiptWriteResult.Inserted,await new OmsPostgresDeribitCommandReceiptJournal(db.Source).RecordAsync(receipt));
        live.NativeReport=receipt.Order;var next=await OmsExecutionGatewayPostgresTests.Prepare(live);return(c,receipt,next);
    }
    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(true,true)]
    public async Task ExplicitOwnedCancelCanRunUnderKillButAckOrUnknownNeverReleasesOriginalRisk(bool kill,bool unknown)
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new OmsExecutionGatewayPostgresTests.Root();await root.Bind(db.Source);
        await using var live=await Live.Open(db);var(c,receipt,recovery)=await Seed(db,live);
        if(kill)
        {
            await using(var q=db.Source.CreateCommand("INSERT INTO oms_risk_control_permissions_v1 VALUES(3,'tenant','DERIBIT',3,'account','',1,current_user,true,false)"))await q.ExecuteNonQueryAsync();
            await new OmsPostgresRiskControls(db.Source).SetAsync(new(OmsRiskControlScopeV1.ForAccount(Scope()),OmsRiskControlKind.KillSwitch,Guid.NewGuid(),0,true,"Operator test stop"));
        }
        var wire=new OmsExecutionGatewayPostgresTests.Wire{LoseAck=unknown};
        await using var gateway=await OmsPostgresExecutionGateway.OpenAsync(Scope(),db.Source,recovery,new Inputs(),root.PathName,_=>Task.FromResult<IOmsAccountOrderTransport>(wire),cancelPolicies:new Policies());
        var result=await gateway.SubmitAsync(DeribitCommandReceiptFixtures.Cancel(c,receipt.Order.ExchangeOrderId));
        Assert.Equal(unknown?OmsExecutionResultKind.Unknown:OmsExecutionResultKind.Acknowledged,result.Kind);Assert.Equal(1,wire.Writes);
        Assert.Equal(1,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_cancel_admissions_v1"));
        Assert.Equal(1,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_reservations_v1"));Assert.Equal(0,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_settlements_v1"));
    }
    [Theory]
    [InlineData("foreign-id")] [InlineData("foreign-aggregate")] [InlineData("changed-quantity")] [InlineData("missing-policy")]
    public async Task NoRiskFreeCancelBypassExistsForForeignOrUnapprovedTerms(string problem)
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();await using var live=await Live.Open(db);var(c,receipt,r)=await Seed(db,live);
        var cancel=DeribitCommandReceiptFixtures.Cancel(c,receipt.Order.ExchangeOrderId);
        cancel=problem switch{
            "foreign-id"=>cancel with{Payload=cancel.Payload with{TargetExchangeOrderId="someone-else"}},
            "foreign-aggregate"=>cancel with{LocalOrderId=new(Guid.NewGuid())},
            "changed-quantity"=>cancel with{Payload=cancel.Payload with{Quantity=2}},_=>cancel};
        Assert.Equal(OmsRiskAdmissionResult.Denied,await new OmsPostgresRiskAdmission(db.Source,new Inputs(),cancelPolicies:problem=="missing-policy"?null:new Policies()).AdmitAsync(cancel,r.Current!.Publication,r.Current.Qualified));
        Assert.Equal(0,await OmsExecutionGatewayPostgresTests.Count(db,"oms_risk_cancel_admissions_v1"));Assert.Equal(1,await OmsExecutionGatewayPostgresTests.Count(db,"oms_commands"));
    }
}
