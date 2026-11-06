using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Deribit;
using TradeOps.Worker;
using Xunit;

namespace TradeOps.UnitTests;
public sealed class OmsExecutionHostAndBypassTests
{
    [Fact]
    public async Task LegacyAdapterCannotPlaceOrCancelEvenWithCredentialsAndNoNetworkIsCreated()
    {
        var adapter=new DeribitTestnetExchangeClient(new NoNetwork(),new(){ClientId="test",ClientSecret="test"},NullLogger<DeribitTestnetExchangeClient>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>adapter.PlaceOrderAsync(new("native-label","ETH-PERPETUAL",OrderSide.Buy,OrderType.Limit,1)));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>adapter.CancelOrderAsync("native-id"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>adapter.CancelOrderAsync("native-id","ETH-PERPETUAL"));
    }
    [Fact]
    public void DefaultHostDoesNotRegisterOmsSenderOrReadCredentials()
    {
        var b=DeribitCaptureHostTests.Builder(new());Assert.False(DeribitOmsExecutionStartup.TryConfigure(b));
        Assert.DoesNotContain(b.Services,x=>x.ServiceType==typeof(IOmsOrderIntentGateway));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExecutionHostRequiresExplicitActivationAndSeparateIdentity(bool enabled)
    {
        using var root=new OmsExecutionGatewayPostgresTests.Root();
        var data=DeribitCaptureHostTests.Configuration();data["Worker:Mode"]=DeribitOmsExecutionStartup.Mode;data["DeribitOms:Enabled"]=enabled.ToString();data["DeribitOms:LockRoot"]=root.PathName;
        data["ConnectionStrings:TradeOpsCaptureDb"]="Host=localhost;Database=owned;Username=capture";
        data["ConnectionStrings:TradeOpsRecoveryDb"]="Host=localhost;Database=owned;Username=recovery";
        data["ConnectionStrings:TradeOpsExecutionDb"]="Host=localhost;Database=owned;Username=recovery";
        data["DeribitRecovery:InitialFromUtc"]=DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");
        var b=DeribitCaptureHostTests.Builder(data);Assert.Throws<InvalidOperationException>(()=>DeribitOmsExecutionStartup.TryConfigure(b));
    }
    [Fact]
    public void ExecutionHostComposesOnlyRecoveryRiskAndOwnedGatewayWithOrdinarySwitchesFalse()
    {
        using var root=new OmsExecutionGatewayPostgresTests.Root();var data=DeribitCaptureHostTests.Configuration();
        data["Worker:Mode"]=DeribitOmsExecutionStartup.Mode;data["DeribitOms:Enabled"]="true";data["DeribitOms:LockRoot"]=root.PathName;
        data["ConnectionStrings:TradeOpsCaptureDb"]="Host=localhost;Database=owned;Username=capture";
        data["ConnectionStrings:TradeOpsRecoveryDb"]="Host=localhost;Database=owned;Username=recovery";
        data["ConnectionStrings:TradeOpsExecutionDb"]="Host=localhost;Database=owned;Username=execution";
        data["DeribitRecovery:InitialFromUtc"]=DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");
        var b=DeribitCaptureHostTests.Builder(data);Assert.True(DeribitOmsExecutionStartup.TryConfigure(b));
        Assert.DoesNotContain(b.Services,x=>x.ServiceType==typeof(IExchangeClient)||x.ServiceType==typeof(IExchangeEventStream));
        using var host=b.Build();Assert.IsType<DeribitOmsExecutionWorker>(host.Services.GetRequiredService<IOmsOrderIntentGateway>());
    }
    private sealed class NoNetwork:IHttpClientFactory {public HttpClient CreateClient(string name)=>throw new InvalidOperationException("Unexpected HTTP creation.");}
}
