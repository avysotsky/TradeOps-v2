using TradeOps.Worker;
using Xunit;

namespace TradeOps.UnitTests;
public sealed class DeribitFixNontradingConfigurationTests
{
    [Theory]
    [InlineData("valid")][InlineData("wrong-mode")][InlineData("execution")][InlineData("fix-off")][InlineData("shared-role")]
    [InlineData("other-store")][InlineData("startup-sql")][InlineData("relative-root")][InlineData("ordinary-execution")]
    public void ExclusiveModeAndDistinctConstrainedConnectionsAreRequiredBeforeAnyIo(string mode)
    {
        var data=DeribitCaptureHostTests.Configuration();foreach(var (key,value)in DeribitFixConfigurationCheckTests.Settings())data[key]=value;
        data["Worker:Mode"]=DeribitFixNontradingConnection.Mode;data["DeribitOms:Enabled"]="false";data["DeribitFix:Enabled"]="true";
        if(mode=="wrong-mode")data["Worker:Mode"]=DeribitOmsExecutionStartup.FixMode;
        if(mode=="execution")data["DeribitOms:Enabled"]="true";
        if(mode=="fix-off")data["DeribitFix:Enabled"]="false";
        if(mode=="shared-role")data["ConnectionStrings:TradeOpsFixDb"]=data["ConnectionStrings:TradeOpsExecutionDb"];
        if(mode=="other-store")data["ConnectionStrings:TradeOpsFixDb"]+=";Port=1234";
        if(mode=="startup-sql")data["ConnectionStrings:TradeOpsFixDb"]+=";Options=-c role=admin";
        if(mode=="relative-root")data["DeribitOms:LockRoot"]="relative";
        if(mode=="ordinary-execution")data["DeribitTerminal:ExecutionEnabled"]="true";
        var config=DeribitFixConfigurationCheckTests.Config(data);
        if(mode=="valid")DeribitFixNontradingConnection.ValidateConfiguration(config);
        else Assert.ThrowsAny<Exception>(()=>DeribitFixNontradingConnection.ValidateConfiguration(config));
    }
}
