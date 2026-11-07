using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TradeOps.Infrastructure.Persistence.Oms;
using TradeOps.Worker;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;
public sealed class DeribitFixConfigurationCheckTests
{
    internal static Dictionary<string,string?> Settings()=>new()
    {
        ["ConnectionStrings:TradeOpsCaptureDb"]="Host=127.0.0.1;Database=synthetic;Username=capture;Password=synthetic-secret",
        ["ConnectionStrings:TradeOpsRecoveryDb"]="Host=127.0.0.1;Database=synthetic;Username=recovery;Password=synthetic-secret",
        ["ConnectionStrings:TradeOpsExecutionDb"]="Host=127.0.0.1;Database=synthetic;Username=execution;Password=synthetic-secret",
        ["ConnectionStrings:TradeOpsFixDb"]="Host=127.0.0.1;Database=synthetic;Username=protocol;Password=synthetic-secret",
        ["DeribitOms:LockRoot"]=Path.GetTempPath(),["DeribitFix:SenderCompId"]="synthetic-session",
        ["DeribitCapture:Tenant"]="tenant",["DeribitCapture:Account"]="account",["DeribitRecovery:InitialFromUtc"]=DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O")
    };
    internal static IConfiguration Config(Dictionary<string,string?> data)=>new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    [Fact]
    public async Task MissingAllSettingsReportsEveryBlockerWithoutAnyHostOrCredentialBinding()
    {
        var result=await DeribitFixConfigurationCheck.RunAsync(Config(new()));
        Assert.Equal("FIX_CONFIGURATION_CHECK_BLOCKED",result.Status);Assert.Equal(9,result.Checks.Count);Assert.All(result.Checks,x=>Assert.False(x.Passed));
        Assert.False(result.VaultUsed||result.VenueUsed||result.TradingStarted||result.LiveAuthorityEstablished||result.NativeAccountVerified);
    }
    [Theory]
    [InlineData("ConnectionStrings:TradeOpsCaptureDb")][InlineData("ConnectionStrings:TradeOpsRecoveryDb")]
    [InlineData("ConnectionStrings:TradeOpsExecutionDb")][InlineData("ConnectionStrings:TradeOpsFixDb")]
    [InlineData("DeribitOms:LockRoot")][InlineData("DeribitFix:SenderCompId")][InlineData("DeribitCapture:Tenant")]
    [InlineData("DeribitCapture:Account")][InlineData("DeribitRecovery:InitialFromUtc")]
    public async Task EachMissingSettingStopsBeforeDatabaseInspection(string key)
    {
        var data=Settings();data[key]=null;var result=await DeribitFixConfigurationCheck.RunAsync(Config(data));
        Assert.Equal("Setting:"+key,Assert.Single(result.Checks,x=>!x.Passed).Code);Assert.Equal(9,result.Checks.Count);
        Assert.DoesNotContain("synthetic-secret",JsonSerializer.Serialize(result));
    }
    [Theory]
    [InlineData("malformed")][InlineData("shared-user")][InlineData("other-db")][InlineData("startup-sql")][InlineData("local-time")][InlineData("invalid-session")][InlineData("relative-root")]
    [InlineData("future-anchor")][InlineData("old-anchor")][InlineData("subaccount")][InlineData("history-policy")]
    public async Task UnsafeConfigurationIsSanitizedAndNeverOpensAConnection(string variant)
    {
        var data=Settings();
        if(variant=="history-policy")data["DeribitRecovery:HistoryCoverage"]="invented";
        if(variant=="malformed")data["ConnectionStrings:TradeOpsFixDb"]="invalid-synthetic-secret";
        if(variant=="shared-user")data["ConnectionStrings:TradeOpsFixDb"]=data["ConnectionStrings:TradeOpsExecutionDb"];
        if(variant=="other-db")data["ConnectionStrings:TradeOpsFixDb"]="Host=127.0.0.1;Database=other;Username=protocol";
        if(variant=="startup-sql")data["ConnectionStrings:TradeOpsFixDb"]+=";Options=-c role=owner";
        if(variant=="local-time")data["DeribitRecovery:InitialFromUtc"]=DateTimeOffset.UtcNow.AddMinutes(-5).ToOffset(TimeSpan.FromHours(2)).ToString("O");
        if(variant=="invalid-session")data["DeribitFix:SenderCompId"]="invalid\u0001sender";
        if(variant=="relative-root")data["DeribitOms:LockRoot"]=".";
        if(variant=="future-anchor")data["DeribitRecovery:InitialFromUtc"]=DateTimeOffset.UtcNow.AddMinutes(1).ToString("O");
        if(variant=="old-anchor")data["DeribitRecovery:InitialFromUtc"]=DateTimeOffset.UtcNow.AddDays(-8).ToString("O");
        if(variant=="subaccount")data["DeribitCapture:Subaccount"]="synthetic-sub";
        var result=await DeribitFixConfigurationCheck.RunAsync(Config(data));Assert.Equal("ConfigurationShapeOrInspection",Assert.Single(result.Checks,x=>!x.Passed).Code);
        Assert.DoesNotContain(result.Checks,x=>x.Code=="ReadOnlyConnections");Assert.DoesNotContain("synthetic-secret",JsonSerializer.Serialize(result));
    }
    [Fact]
    public void NodeInspectionDoesNotProvisionOrReserveAFile()
    {
        using var root=new OmsExecutionGatewayPostgresTests.Root();
        Assert.Throws<FileNotFoundException>(()=>OmsExecutionNodeProvisioning.Inspect(root.PathName,Scope()));Assert.Empty(Directory.GetFiles(root.PathName));
        var prepared=OmsExecutionNodeProvisioning.Prepare(root.PathName,Scope());var file=Assert.Single(Directory.GetFiles(root.PathName));var before=File.GetLastWriteTimeUtc(file);
        Assert.Equal(prepared,OmsExecutionNodeProvisioning.Inspect(root.PathName,Scope()));Assert.Equal(before,File.GetLastWriteTimeUtc(file));
        using var owner=new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None);Assert.Equal(0,owner.Length);
    }
}
