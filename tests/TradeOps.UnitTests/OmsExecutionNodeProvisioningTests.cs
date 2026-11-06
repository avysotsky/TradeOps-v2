using TradeOps.Infrastructure.Persistence.Oms;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;
namespace TradeOps.UnitTests;

public sealed class OmsExecutionNodeProvisioningTests
{
    [Fact]
    public void LocalPhysicalIdentitiesAreStableAndPreparationNeverChangesAnExistingFence()
    {
        using var root=new OmsExecutionGatewayPostgresTests.Root();
        var first=OmsExecutionNodeProvisioning.Prepare(root.PathName,Scope());
        var next=OmsExecutionNodeProvisioning.Prepare(root.PathName,Scope("other-logical-account"));
        Assert.Equal(first,next);Assert.Equal(2,first.SchemaVersion);
        Assert.Equal(64,first.NodeSha256.Length);Assert.Equal(64,first.RootSha256.Length);Assert.Equal(64,first.LockSha256.Length);
        var path=Assert.Single(Directory.GetFiles(root.PathName));Assert.Equal(0,new FileInfo(path).Length);
        using var held=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);
        Assert.Throws<IOException>(()=>OmsExecutionNodeProvisioning.Prepare(root.PathName,Scope()));
        Assert.Equal(first.LockSha256,OmsExecutionNodeProvisioning.FileIdentity(held.SafeFileHandle));
    }
}
