using System.Text;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit.Fix;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.UnitTests.Support;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;
using Live=TradeOps.UnitTests.DeribitRecoveryPublicationPostgresTests.Live;
using FrozenClock=TradeOps.UnitTests.OmsRiskAdmissionPostgresTests.FrozenClock;

namespace TradeOps.UnitTests;
public sealed class DeribitFixPhysicalOwnerPostgresTests
{
    [Fact]
    public async Task PhysicalFenceCannotBeReleasedWhileAnEngineSessionWriteIsStillPending()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new OmsExecutionGatewayPostgresTests.Root();await root.Bind(db.Source);
        await using var owner=await OmsSingleHostPhysicalFence.AcquireAsync(db.Source,Scope(),root.PathName);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writing=owner.CreateSessionAuthority().InitiateAsync(()=>release.Task);
        var disposing=owner.DisposeAsync().AsTask();
        try{Assert.False(disposing.IsCompleted);await Assert.ThrowsAsync<IOException>(()=>OmsSingleHostPhysicalFence.AcquireAsync(db.Source,Scope(),root.PathName));}
        finally{release.TrySetResult();}
        await writing;await disposing;
        await using var next=await OmsSingleHostPhysicalFence.AcquireAsync(db.Source,Scope(),root.PathName);Assert.True(next.IsCurrent);
    }
    [Fact]
    public async Task ActualOsOwnerAndLiveB4PermitProtectTheActualQuickFixResponder()
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new OmsExecutionGatewayPostgresTests.Root();await root.Bind(db.Source);
        await using var owner=await OmsSingleHostPhysicalFence.AcquireAsync(db.Source,Scope(),root.PathName);
        var clock=new FrozenClock();await using var live=await Live.Open(db,clock);var baseline=await OmsExecutionGatewayPostgresTests.Prepare(live);
        using var r=new DeribitQuickFixSessionTests.Rig(actualAuthority:owner.CreateSessionAuthority());r.Logon();var c=DeribitFixCommandTests.Command();
        var permit=baseline.Current!.Qualified.CreatePhysicalWritePermit(c,()=>owner.IsCurrent);
        r.Engine.SendCommand(c,permit,DeribitFixCommandTests.Metadata());var sent=Assert.Single(r.Sent,x=>x.Header.GetString(35)=="D");
        Assert.Equal(c.Payload.Quantity,sent.GetDecimal(38));Assert.Equal(DeribitFixCommandEncoderV1.ClientId(c),sent.GetString(11));
        // Test-only friend assembly creates this live permit; it is not a production risk admission.
        Assert.Throws<InvalidOperationException>(()=>r.Engine.SendCommand(c,permit,DeribitFixCommandTests.Metadata()));
        Assert.Single(r.Sent,x=>x.Header.GetString(35)=="D");
    }
    [Theory]
    [InlineData("owner")][InlineData("ingress")]
    public async Task RevokedOwnerOrNewIngressStopsPhysicalFixWriteDespiteAlreadyPersistedEngineSequence(string reason)
    {
        await using var db=await OmsPostgresTestDatabase.CreateAsync();using var root=new OmsExecutionGatewayPostgresTests.Root();await root.Bind(db.Source);
        await using var owner=await OmsSingleHostPhysicalFence.AcquireAsync(db.Source,Scope(),root.PathName);
        var clock=new FrozenClock();await using var live=await Live.Open(db,clock);var baseline=await OmsExecutionGatewayPostgresTests.Prepare(live);
        using var r=new DeribitQuickFixSessionTests.Rig(actualAuthority:owner.CreateSessionAuthority());r.Logon();var c=DeribitFixCommandTests.Command();
        var permit=baseline.Current!.Qualified.CreatePhysicalWritePermit(c,()=>owner.IsCurrent);
        if(reason=="owner")owner.Revoke();
        else{var epoch=baseline.Current.Qualified.Start.Epoch;await live.Coordinator.RegisterReceiptAsync(live.Session,epoch,OmsPrivateCaptureReplayTests.Event(epoch));}
        Assert.Throws<InvalidOperationException>(()=>r.Engine.SendCommand(c,permit,DeribitFixCommandTests.Metadata()));
        Assert.DoesNotContain(r.Sent,x=>x.Header.GetString(35) is "D" or "F");Assert.Equal(3UL,r.Store.NextSender);Assert.False(r.Engine.LoggedOn);
    }
}
