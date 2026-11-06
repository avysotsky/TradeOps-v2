using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;
using Live=TradeOps.UnitTests.DeribitRecoveryPublicationPostgresTests.Live;
using Inputs=TradeOps.UnitTests.OmsRiskAdmissionPostgresTests.Inputs;
namespace TradeOps.UnitTests;
public sealed class OmsPendingRiskReaderPostgresTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PendingTermsAreScopedHashedAndNeverGuessNativeUnits(bool unsupported)
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();await using var live=await Live.Open(db);var r=await OmsExecutionGatewayPostgresTests.Prepare(live);
        var c=OmsExecutionGatewayPostgresTests.Command();if(unsupported)c=c with{Payload=c.Payload with{QuantityUnit=OmsQuantityUnit.BaseAsset}};
        Assert.Equal(OmsRiskAdmissionResult.Reserved,await new OmsPostgresRiskAdmission(db.Source,new Inputs()).AdmitAsync(c,r.Current!.Publication,r.Current.Qualified));
        var reader=new OmsPostgresPendingRiskReader(db.Source);
        Assert.Empty(await reader.ReadAsync(Scope(),c.CommandId));Assert.Empty(await reader.ReadAsync(Scope("other"),new(Guid.NewGuid())));
        if(unsupported)await Assert.ThrowsAsync<InvalidOperationException>(()=>reader.ReadAsync(Scope(),new(Guid.NewGuid())));
        else{var pending=Assert.Single(await reader.ReadAsync(Scope(),new(Guid.NewGuid())));Assert.Equal(c.Payload.Quantity,pending.NativeUsd);Assert.Equal(c.Payload.LimitPrice,pending.LimitPrice);}
    }
}
