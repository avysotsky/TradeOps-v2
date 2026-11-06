using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit.Fix;
using Xunit;
namespace TradeOps.UnitTests;

public sealed class DeribitFixTransportRefusalTests
{
    [Theory]
    [InlineData("wrong-pin")][InlineData("contract-ten")][InlineData("changed-pin")][InlineData("disconnect")]
    public async Task NativeAccountAndActualMetadataAreRequiredBeforeAnyOrderBytes(string mode)
    {
        await using var capture=await OmsPrivateAccountRevisionTests.Setup.Open();
        var key=OmsFixSessionStorePostgresTests.Key("refusal-"+Guid.NewGuid().ToString("N"));
        using var store=new DeribitQuickFixSessionTests.MemoryStore();
        using var handler=new DeribitFixGatewayPostgresTests.Http{Account=mode=="wrong-pin"?999:123,Contract=mode=="contract-ten"?10:1};
        using var http=new HttpClient(handler);var wire=new DeribitFixGatewayPostgresTests.Wire(key);var opened=0;var recoveries=0;
        Task<DeribitOmsFixTransportV1> Open()=>DeribitOmsFixTransportV1.OpenCoreAsync(DeribitFixGatewayPostgresTests.Binding(),new(key),store,
            new NativeJournal(),new OmsPhysicalSessionAuthority(write=>write()),()=>capture.Coordinator.BeginExternalIngress(capture.Session),http,
            (_,_)=>Task.FromResult<DeribitNativeCommandView?>(null),()=>recoveries++,ct=>{opened++;return Task.FromResult<IDeribitFixWireV1>(wire);},CancellationToken.None);
        if(mode=="wrong-pin")
        {await Assert.ThrowsAsync<InvalidOperationException>(Open);Assert.Equal(0,opened);Assert.Equal(0,wire.Placements);return;}
        await using var transport=await Open();
        if(mode=="changed-pin")handler.Account=999;
        if(mode=="disconnect")
        {
            wire.Abort();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while(transport.IsRunning)await Task.Delay(5,deadline.Token);
            Assert.True(recoveries>0);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(()=>transport.PrepareAsync(DeribitFixCommandTests.Command(),CancellationToken.None));
        Assert.Equal(0,wire.Placements);Assert.Equal(0,wire.Cancels);
    }
    private sealed class NativeJournal:IDeribitFixNativeReportJournal
    {public DeribitFixReportWriteResult Record(DeribitFixReportEnvelopeV1 r)=>throw new InvalidOperationException("No economic report expected.");}
}
