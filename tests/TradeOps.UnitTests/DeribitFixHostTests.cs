using Microsoft.Extensions.DependencyInjection;
using TradeOps.Application.Oms;
using TradeOps.Application.Interfaces;
using TradeOps.Worker;
using Xunit;
namespace TradeOps.UnitTests;

public sealed class DeribitFixHostTests
{
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
        Assert.Null(host.Services.GetService<IExchangeClient>());Assert.Null(host.Services.GetService<IExchangeEventStream>());
    }
}
