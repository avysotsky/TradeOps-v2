using System.Text.Json;
using Xunit;
namespace TradeOps.UnitTests;

public sealed class LegacyOmsEntryPointTests
{
    [Theory]
    [InlineData("testnet")] [InlineData("mainnet")] [InlineData("bounded")]
    public async Task OldExecutableCannotReadTradingInputOrEnterRawOrderRunner(string mode)
    {
        var assembly=mode=="testnet"?typeof(TradeOps.DeribitTestnetOrderQualification.TestnetOrderQualification).Assembly:
            typeof(TradeOps.DeribitMainnetEthQualification.BoundedOmsScenario).Assembly;
        var original=Console.Out;using var output=new StringWriter();
        try
        {
            Console.SetOut(output);
            var method=assembly.EntryPoint!.DeclaringType!.GetMethod("Main",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static)!;
            var task=(Task<int>)method.Invoke(null,[mode=="bounded"?new[]{"--owner-bounded-oms"}:Array.Empty<string>()])!;
            Assert.Equal(1,await task.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally{Console.SetOut(original);}
        using var doc=JsonDocument.Parse(output.ToString());var root=doc.RootElement;
        Assert.Equal(0,root.GetProperty("PlacementAttempts").GetInt32());Assert.Equal(0,root.GetProperty("CancellationAttempts").GetInt32());Assert.False(root.GetProperty("TradingReady").GetBoolean());
        Assert.Equal(mode switch{"testnet"=>"TESTNET_ORDER_OMS_GATEWAY_REQUIRED","mainnet"=>"MAINNET_ETH_OMS_GATEWAY_REQUIRED",_=>"OMS_SCENARIO_REFUSED"},root.GetProperty("Status").GetString());
    }
}
