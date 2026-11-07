using Microsoft.Extensions.DependencyInjection;
using TradeOps.Application.Oms;
using TradeOps.Application.Interfaces;
using TradeOps.Worker;
using Xunit;
namespace TradeOps.UnitTests;

public sealed class DeribitFixHostTests
{
    [Theory]
    [InlineData("valid")][InlineData("not-owner")][InlineData("testnet")][InlineData("source-mismatch")][InlineData("no-source")]
    public void QualificationModeRequiresOwnerMainnetPinAndExactSource(string mode)
    {
        using var root=new OmsExecutionGatewayPostgresTests.Root();var data=DeribitRecoveryHostStartupTests.Configuration();
        data["Worker:Mode"]=DeribitOmsExecutionStartup.QualificationMode;data["DeribitOms:Enabled"]="true";data["DeribitOms:LockRoot"]=root.PathName;
        data["DeribitFix:Enabled"]="true";data["DeribitFix:SenderCompId"]="owned-fix-session";
        data["DeribitCapture:Environment"]=mode=="testnet"?"Testnet":"Mainnet";
        data["DeribitFixQualification:OwnerLaunch"]=mode=="not-owner"?"false":"true";
        data["DeribitFixQualification:ExpectedSourceCommit"]=mode=="no-source"?null:new string('a',40);
        data["DeribitFixQualification:ObservedSourceCommit"]=new string(mode=="source-mismatch"?'b':'a',40);
        foreach(var (name,user) in new[]{("Capture","capture"),("Recovery","recovery"),("Execution","execution"),("Fix","protocol")})
            data["ConnectionStrings:TradeOps"+name+"Db"]="Host=127.0.0.1;Database=synthetic;Username="+user;
        var b=DeribitCaptureHostTests.Builder(data);
        if(mode!="valid"){Assert.Throws<InvalidOperationException>(()=>DeribitOmsExecutionStartup.TryConfigure(b));return;}
        Assert.True(DeribitOmsExecutionStartup.TryConfigure(b));using var host=b.Build();
        Assert.Single(host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<DeribitFixQualificationWorker>());
        Assert.NotNull(host.Services.GetRequiredService<DeribitFixQualificationHostOptions>());
    }
    [Theory]
    [InlineData("valid")][InlineData("disabled")][InlineData("missing-protocol")][InlineData("same-capture")]
    [InlineData("same-recovery")][InlineData("same-execution")][InlineData("other-db")][InlineData("no-session")]
    public void OptInHostRequiresFourDistinctDatabaseIdentitiesAndPinnedSession(string mode)
    {
        using var root=new OmsExecutionGatewayPostgresTests.Root();var data=DeribitRecoveryHostStartupTests.Configuration();
        data["Worker:Mode"]=DeribitOmsExecutionStartup.FixMode;data["DeribitOms:Enabled"]="true";data["DeribitOms:LockRoot"]=root.PathName;
        data["DeribitFix:Enabled"]=mode=="disabled"?"false":"true";data["DeribitFix:SenderCompId"]=mode=="no-session"?null:"owned-fix-session";
        foreach(var (name,user) in new[]{("Capture","capture"),("Recovery","recovery"),("Execution","execution"),("Fix","protocol")})
            data["ConnectionStrings:TradeOps"+name+"Db"]="Host=127.0.0.1;Database=synthetic;Username="+user;
        data["ConnectionStrings:TradeOpsFixDb"]=mode switch{
            "missing-protocol"=>null,"same-capture"=>data["ConnectionStrings:TradeOpsCaptureDb"],"same-recovery"=>data["ConnectionStrings:TradeOpsRecoveryDb"],
            "same-execution"=>data["ConnectionStrings:TradeOpsExecutionDb"],"other-db"=>"Host=127.0.0.1;Database=other;Username=protocol",_=>data["ConnectionStrings:TradeOpsFixDb"]};
        var b=DeribitCaptureHostTests.Builder(data);
        if(mode!="valid"){Assert.Throws<InvalidOperationException>(()=>DeribitOmsExecutionStartup.TryConfigure(b));return;}
        Assert.True(DeribitOmsExecutionStartup.TryConfigure(b));using var host=b.Build();
        Assert.IsType<DeribitOmsExecutionWorker>(host.Services.GetRequiredService<IOmsOrderIntentGateway>());
        Assert.NotNull(host.Services.GetRequiredService<DeribitOmsFixProtocolSource>());
        Assert.Empty(host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<DeribitFixQualificationWorker>());
        Assert.Null(host.Services.GetService<IExchangeClient>());Assert.Null(host.Services.GetService<IExchangeEventStream>());
    }
}
