using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Services;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange;
using TradeOps.Worker;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class ReconciliationShadowRuntimeCanaryTests
{
    private static readonly IBrokerReconciliationDecisionEngine Engine =
        new BrokerReconciliationDecisionEngine(new OrderStateMachine());

    [Fact]
    public async Task RealMockAndIbkrOfflineAdapters_EvaluateWithoutPersistenceOrNetwork()
    {
        var summary = await OfflineReconciliationShadowCanary.RunAsync(Engine);

        Assert.Equal(2, summary.Providers);
        Assert.Equal(6, summary.Evaluations);
        Assert.Equal(2, summary.Candidates);
        Assert.Equal(4, summary.Unresolved);
        Assert.Equal(0, summary.DatabaseWrites);
        Assert.Equal(0, summary.BrokerRequests);
    }

    [Fact]
    public async Task CanaryRepeatedCalls_AreDeterministicAndDoNotShareOrderState()
    {
        var first = await OfflineReconciliationShadowCanary.RunAsync(Engine);
        var second = await OfflineReconciliationShadowCanary.RunAsync(Engine);

        Assert.Equal(first, second);
        Assert.Equal(6, second.Evaluations);
    }

    [Fact]
    public async Task RealHostedService_StartsOnceAndExposesNoTradingSurface()
    {
        var worker = new ReconciliationShadowCanaryHostedService(
            Engine, NullLogger<ReconciliationShadowCanaryHostedService>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(
            typeof(ReconciliationShadowCanaryHostedService).GetMethods(),
            method => method.Name.Contains("PlaceOrder", StringComparison.Ordinal)
                   || method.Name.Contains("CancelOrder", StringComparison.Ordinal)
                   || method.Name.Contains("RetryOrder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationFailsClosedWithoutProducingCanarySuccess()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => OfflineReconciliationShadowCanary.RunAsync(Engine, cts.Token));

        var worker = new ReconciliationShadowCanaryHostedService(
            Engine, NullLogger<ReconciliationShadowCanaryHostedService>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => worker.StartAsync(cts.Token));
    }

    [Theory]
    [InlineData("Mock", ReconciliationShadowStartupPolicy.RequiredConfirm)]
    public void Enabled_ShadowCanary_OnlyWithMockAndExplicitConfirmation(
        string provider, string confirmation)
    {
        ReconciliationShadowStartupPolicy.DemandSyntheticOnly(
            enabled: true, provider, confirmation);
    }

    [Theory]
    [InlineData("IbkrPaper", ReconciliationShadowStartupPolicy.RequiredConfirm)]
    [InlineData("Deribit", ReconciliationShadowStartupPolicy.RequiredConfirm)]
    [InlineData("Bybit", ReconciliationShadowStartupPolicy.RequiredConfirm)]
    [InlineData("mock", ReconciliationShadowStartupPolicy.RequiredConfirm)]
    [InlineData("Mock", null)]
    [InlineData("Mock", "")]
    [InlineData("Mock", "TRUE")]
    [InlineData(null, ReconciliationShadowStartupPolicy.RequiredConfirm)]
    public void Enabled_ShadowCanary_FailsClosedWithoutSafetyPreconditions(
        string? provider, string? confirmation)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ReconciliationShadowStartupPolicy.DemandSyntheticOnly(
                enabled: true, provider, confirmation));
    }

    [Theory]
    [InlineData("IbkrPaper", null)]
    [InlineData("Bybit", "anything")]
    [InlineData(null, null)]
    public void Disabled_ShadowCanary_DoesNotApplyAnyRuntimeRestrictions(
        string? provider, string? confirmation)
    {
        ReconciliationShadowStartupPolicy.DemandSyntheticOnly(
            enabled: false, provider, confirmation);
    }

    [Fact]
    public void WorkerDefaultConfiguration_RequiresShadowDisabled()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "TradeOps.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var workerJson = File.ReadAllText(Path.Combine(
            directory!.FullName, "src", "TradeOps.Worker", "appsettings.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(workerJson);

        Assert.False(doc.RootElement.GetProperty("ReconciliationShadow")
            .GetProperty("Enabled").GetBoolean());
    }

    [Fact]
    public void RealRuntimeWorkerHasNoBrokerCommandsOrDbDependencies()
    {
        // Static reflectable guard in addition to the executable runtime
        // path tested by the owner on the opt-in isolated Worker container.
        var constructor = typeof(ReconciliationShadowCanaryHostedService)
            .GetConstructors().Single();
        Assert.Equal(2, constructor.GetParameters().Length);
        Assert.Contains(constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(IBrokerReconciliationDecisionEngine));
        Assert.DoesNotContain(constructor.GetParameters(),
            parameter => parameter.ParameterType.Name.Contains("DbContext",
                    StringComparison.Ordinal)
                || parameter.ParameterType.Name.Contains("Repository",
                    StringComparison.Ordinal)
                || parameter.ParameterType.Name.Contains("ExchangeClient",
                    StringComparison.Ordinal));
    }
}
