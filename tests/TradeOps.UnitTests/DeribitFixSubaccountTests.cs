using Microsoft.Extensions.DependencyInjection;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit.Fix;
using TradeOps.Domain.Oms;
using TradeOps.Worker;
using Xunit;

namespace TradeOps.UnitTests;
public sealed class DeribitFixSubaccountTests
{
    internal static AccountKey Scope()=>new(new("tenant"),new("DERIBIT"),OmsEnvironment.Testnet,new("account"),new TradingSubaccountId("account"));
    [Theory]
    [InlineData("valid")][InlineData("v1")][InlineData("v2-main")][InlineData("parent-selection")][InlineData("unknown-schema")]
    public void VersionTwoRepresentsOnlyADirectStandaloneSubaccountCredentialScope(string mode)
    {
        var key=OmsFixSessionStorePostgresTests.Key() with{SchemaVersion=2,Scope=Scope()};
        key=mode switch{"v1"=>key with{SchemaVersion=1},"v2-main"=>key with{Scope=OmsFixSessionStorePostgresTests.Key().Scope},
            "parent-selection"=>key with{Scope=new(Scope().Tenant,Scope().Venue,Scope().Environment,new("parent"),Scope().Subaccount)},
            "unknown-schema"=>key with{SchemaVersion=3},_=>key};
        if(mode=="valid")key.Validate();else Assert.Throws<ArgumentException>(key.Validate);
    }
    [Theory]
    [InlineData("valid")][InlineData("wrong-id")][InlineData("main-key")]
    public async Task DirectCredentialIdentityIsVerifiedBeforeAnyFixConnection(string mode)
    {
        await using var capture=await OmsPrivateAccountRevisionTests.Setup.Open();
        var key=OmsFixSessionStorePostgresTests.Key("sub-"+Guid.NewGuid().ToString("N")) with{SchemaVersion=2,Scope=Scope()};
        using var store=new DeribitQuickFixSessionTests.MemoryStore();
        using var handler=new DeribitFixGatewayPostgresTests.Http{AccountType=mode=="main-key"?"main":"subaccount",Account=mode=="wrong-id"?999:123};
        using var http=new HttpClient(handler);var wire=new DeribitFixGatewayPostgresTests.Wire(key);var opened=0;
        Task<DeribitOmsFixTransportV1> Open()=>DeribitOmsFixTransportV1.OpenCoreAsync(DeribitFixGatewayPostgresTests.Binding(Scope()),new(key),store,
            new Journal(),new OmsPhysicalSessionAuthority(write=>write()),()=>capture.Coordinator.BeginExternalIngress(capture.Session),http,
            (_,_)=>Task.FromResult<DeribitNativeCommandView?>(null),()=>{},_=>{opened++;return Task.FromResult<IDeribitFixWireV1>(wire);},default);
        if(mode!="valid"){await Assert.ThrowsAsync<InvalidOperationException>(Open);Assert.Equal(0,opened);return;}
        await using var transport=await Open();Assert.Equal(1,opened);Assert.Equal(0,wire.Placements);Assert.Equal(0,wire.Cancels);
    }
    [Theory]
    [InlineData("fix")][InlineData("qualification")][InlineData("ws-refused")]
    public void OptInSelectsVersionTwoForThePinnedSubaccountWithoutEnablingWsExecution(string mode)
    {
        using var root=new OmsExecutionGatewayPostgresTests.Root();var data=DeribitRecoveryHostStartupTests.Configuration();
        data["Worker:Mode"]=mode=="fix"?DeribitOmsExecutionStartup.FixMode:mode=="qualification"?DeribitOmsExecutionStartup.QualificationMode:DeribitOmsExecutionStartup.Mode;
        data["DeribitOms:Enabled"]="true";data["DeribitOms:LockRoot"]=root.PathName;data["DeribitFix:Enabled"]="true";data["DeribitFix:SenderCompId"]="scoped-sub-session";
        data["DeribitCapture:ExpectedAccountType"]="subaccount";data["DeribitCapture:Subaccount"]=data["DeribitCapture:Account"];
        if(mode=="qualification"){data["DeribitCapture:Environment"]="Mainnet";data["DeribitFixQualification:OwnerLaunch"]="true";data["DeribitFixQualification:ExpectedSourceCommit"]=new string('a',40);data["DeribitFixQualification:ObservedSourceCommit"]=new string('a',40);}
        foreach(var (name,user) in new[]{("Capture","capture"),("Recovery","recovery"),("Execution","execution"),("Fix","protocol")})data["ConnectionStrings:TradeOps"+name+"Db"]="Host=127.0.0.1;Database=synthetic;Username="+user;
        var builder=DeribitCaptureHostTests.Builder(data);
        if(mode=="ws-refused"){Assert.Throws<InvalidOperationException>(()=>DeribitOmsExecutionStartup.TryConfigure(builder));return;}
        Assert.True(DeribitOmsExecutionStartup.TryConfigure(builder));using var host=builder.Build();
        var key=host.Services.GetRequiredService<DeribitOmsFixHostOptions>().Session;Assert.Equal(2,key.SchemaVersion);Assert.Equal(key.Scope.Account.Value,key.Scope.Subaccount!.Value.Value);
    }
    private sealed class Journal:IDeribitFixNativeReportJournal
    {public DeribitFixReportWriteResult Record(DeribitFixReportEnvelopeV1 r)=>throw new InvalidOperationException("No order expected.");}
}
