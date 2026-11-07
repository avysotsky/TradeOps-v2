using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Oms;
using TradeOps.Worker;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitRecoveryHostStartupTests
{
    [Theory]
    [InlineData(null)][InlineData(DeribitCollateralHistoryCoverageV2.PolicyName)][InlineData("invented")]
    public void HistoryCoverageIsAnExplicitVersionedHostChoice(string? value)
    {
        var data=Configuration();if(value is not null)data["DeribitRecovery:HistoryCoverage"]=value;
        var builder=DeribitCaptureHostTests.Builder(data);
        if(value=="invented"){Assert.Throws<InvalidOperationException>(()=>DeribitRecoveryStartup.TryConfigure(builder));return;}
        Assert.True(DeribitRecoveryStartup.TryConfigure(builder));using var host=builder.Build();
        Assert.Equal(value is null?DeribitHistoryCoverageMode.InventoryCurrenciesV1:DeribitHistoryCoverageMode.ListedDerivativeSettlementsV2,
            host.Services.GetRequiredService<DeribitRecoveryHostOptions>().HistoryCoverage);
    }
    [Fact]
    public void ExistingModesDoNotReadRecoverySettings()
    {
        foreach (var mode in new[] { null, "Legacy", DeribitCaptureStartup.Mode })
            Assert.False(DeribitRecoveryStartup.TryConfigure(DeribitCaptureHostTests.Builder(new() { ["Worker:Mode"] = mode })));
    }

    [Fact]
    public void ExplicitModeComposesOnlyRecoveryAndKeepsSourcesDistinctAndDiagnosticsSanitized()
    {
        var builder = DeribitCaptureHostTests.Builder(Configuration()); Assert.True(DeribitRecoveryStartup.TryConfigure(builder));
        using var host = builder.Build(); Assert.IsType<DeribitAccountRecoveryWorker>(Assert.Single(host.Services.GetServices<IHostedService>()));
        Assert.Null(host.Services.GetService<IExchangeClient>()); Assert.Null(host.Services.GetService<IExchangeEventStream>());
        var sources = host.Services.GetRequiredService<DeribitRecoverySources>();
        var c = new NpgsqlConnectionStringBuilder(sources.Capture.ConnectionString); var r = new NpgsqlConnectionStringBuilder(sources.Recovery.ConnectionString);
        Assert.NotEqual(c.Username, r.Username); Assert.Equal(c.Database, r.Database);
        Assert.False(r.IncludeErrorDetail); Assert.False(r.LogParameters); Assert.Equal("public", r.SearchPath);
    }

    [Theory]
    [InlineData("DeribitTerminal:ExecutionEnabled", "true")]
    [InlineData("DeribitTerminal:MainnetExecutionEnabled", "true")]
    [InlineData("DeribitTerminal:ExecutionEnabled", null)]
    [InlineData("DeribitTerminal:MainnetExecutionEnabled", null)]
    public void TradingSwitchesMustBeExplicitlyFalse(string key, string? value)
    {
        var data = Configuration(); data[key] = value;
        Assert.Throws<InvalidOperationException>(() => DeribitRecoveryStartup.TryConfigure(DeribitCaptureHostTests.Builder(data)));
    }

    [Theory]
    [InlineData("same-role")] [InlineData("other-database")] [InlineData("malformed")] [InlineData("missing")]
    public void WrongOrSharedDatabaseIdentityRefusesStartup(string mode)
    {
        var data = Configuration();
        data["ConnectionStrings:TradeOpsRecoveryDb"] = mode switch {
            "same-role" => data["ConnectionStrings:TradeOpsCaptureDb"], "other-database" => "Host=127.0.0.1;Database=other;Username=recovery",
            "malformed" => "synthetic-secret-invalid", _ => null };
        var failure = Assert.Throws<InvalidOperationException>(() => DeribitRecoveryStartup.TryConfigure(DeribitCaptureHostTests.Builder(data)));
        Assert.DoesNotContain("synthetic-secret", failure.ToString());
    }

    [Theory]
    [InlineData(null)] [InlineData("2026-01-01T00:00:00Z")] [InlineData("2099-01-01T00:00:00Z")]
    [InlineData("2026-11-05T12:00:00+03:00")]
    public void InitialOutageAnchorCannotBeMissingInvalidOrSilentlyReset(string? anchor)
    {
        var data = Configuration(); data["DeribitRecovery:InitialFromUtc"] = anchor;
        Assert.Throws<InvalidOperationException>(() => DeribitRecoveryStartup.TryConfigure(DeribitCaptureHostTests.Builder(data)));
    }

    [Fact]
    public void ExistingLegacyWorkerRefusesMixedComposition()
    {
        var builder = DeribitCaptureHostTests.Builder(Configuration()); builder.Services.AddHostedService<ExchangeEventWorker>();
        Assert.Throws<InvalidOperationException>(() => DeribitRecoveryStartup.TryConfigure(builder));
    }

    internal static Dictionary<string, string?> Configuration()
    {
        var data = DeribitCaptureHostTests.Configuration(); data["Worker:Mode"] = DeribitRecoveryStartup.Mode;
        data["ConnectionStrings:TradeOpsRecoveryDb"] = "Host=127.0.0.1;Database=synthetic;Username=recovery;Password=synthetic-secret";
        data["DeribitRecovery:InitialFromUtc"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"); return data;
    }
}
