using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TradeOps.Application.Interfaces;
using TradeOps.Connectors.Deribit;
using TradeOps.Worker;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitCaptureHostTests
{
    [Fact]
    public void DefaultMode_PreservesLegacy_WithoutReadingCaptureSecrets()
    {
        var builder = Builder(new());
        Assert.False(DeribitCaptureStartup.TryConfigure(builder));
        builder.Configuration["Worker:Mode"] = "Legacy";
        Assert.False(DeribitCaptureStartup.TryConfigure(builder));
    }

    [Theory]
    [InlineData("")]
    [InlineData("deribit")]
    [InlineData("true")]
    [InlineData("DeribitReadOnlyCapture ")]
    public void UnknownMode_NeverFallsBackToLegacy(string mode)
    {
        var builder = Builder(new() { ["Worker:Mode"] = mode });
        Assert.Throws<InvalidOperationException>(() => DeribitCaptureStartup.TryConfigure(builder));
    }

    [Theory]
    [InlineData("Testnet", false, "test.deribit.com")]
    [InlineData("Mainnet", false, "www.deribit.com")]
    [InlineData("Testnet", true, "test.deribit.com")]
    [InlineData("Mainnet", true, "www.deribit.com")]
    public void Binding_PinsFullScopeAccountAndOfficialEndpoint(string environment, bool sub, string host)
    {
        var data = Configuration();
        data["DeribitCapture:Environment"] = environment;
        data["DeribitCapture:ExpectedAccountType"] = sub ? "subaccount" : "main";
        if (sub) data["DeribitCapture:Subaccount"] = "sub-pinned";
        data["DeribitCapture:WebSocketUrl"] = "wss://untrusted.invalid/ws/api/v2"; // not an accepted override
        var binding = DeribitCaptureStartup.ReadBinding(Builder(data).Configuration);
        Assert.Equal("tenant", binding.Scope.Tenant.Value);
        Assert.Equal("DERIBIT", binding.Scope.Venue.Value);
        Assert.Equal("account", binding.Scope.Account.Value);
        Assert.Equal(sub ? "sub-pinned" : null, binding.Scope.Subaccount?.Value);
        Assert.Equal(sub, binding.Plan.IsSubaccount);
        Assert.Equal(123, binding.Plan.ExpectedAccountId);
        Assert.Equal(host, new Uri(binding.Options.WebSocketUrl).Host);
    }

    [Theory]
    [InlineData("ExecutionEnabled", null)]
    [InlineData("ExecutionEnabled", "true")]
    [InlineData("ExecutionEnabled", "invalid")]
    [InlineData("MainnetExecutionEnabled", null)]
    [InlineData("MainnetExecutionEnabled", "true")]
    [InlineData("MainnetExecutionEnabled", "invalid")]
    public void BothExecutionSwitches_MustBeExplicitlyFalse(string key, string? value)
    {
        var data = Configuration();
        data["DeribitTerminal:" + key] = value;
        var error = Assert.Throws<InvalidOperationException>(() => DeribitCaptureStartup.TryConfigure(Builder(data)));
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("Tenant", " tenant")]
    [InlineData("ExpectedAccountId", "0")]
    [InlineData("ExpectedAccountType", "subaccount")]
    [InlineData("Environment", "3")]
    [InlineData("Instruments:0:AmountUnit", "3")]
    [InlineData("Instruments:0:AmountCurrency", "EUR")]
    [InlineData("Instruments:0:Name", "")]
    public void InvalidPins_FailBeforeAnyConnection_WithoutLeakingSecrets(string field, string value)
    {
        var data = Configuration();
        data["DeribitCapture:" + field] = value;
        var error = Assert.Throws<InvalidOperationException>(() => DeribitCaptureStartup.TryConfigure(Builder(data)));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic-secret", error.ToString());
        Assert.DoesNotContain(value.Length > 2 ? value : "synthetic-client", error.Message);
    }

    [Fact]
    public void Binding_IsSnapshot_ConfigReloadCannotSwitchAccountOrCredentials()
    {
        var builder = Builder(Configuration());
        var binding = DeribitCaptureStartup.ReadBinding(builder.Configuration);
        builder.Configuration["DeribitCapture:Account"] = "another-account";
        builder.Configuration["DeribitCapture:ClientSecret"] = "another-secret";
        builder.Configuration["DeribitCapture:Instruments:0:Name"] = "another-instrument";
        Assert.Equal("account", binding.Scope.Account.Value);
        Assert.Equal("synthetic-secret", binding.Options.ClientSecret);
        Assert.Contains("BTC-PERPETUAL", binding.Plan.Instruments.Keys);
    }

    [Fact]
    public void Composition_RegistersOnlyCaptureWorker_NoLegacyProcessorsOrSenders()
    {
        var builder = Builder(Configuration());
        builder.Services.Configure<HostOptions>(options =>
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
        Assert.True(DeribitCaptureStartup.TryConfigure(builder));
        using var host = builder.Build();
        Assert.IsType<DeribitReadOnlyCaptureWorker>(Assert.Single(host.Services.GetServices<IHostedService>()));
        Assert.Null(host.Services.GetService<IExchangeClient>());
        Assert.Null(host.Services.GetService<IExchangeEventStream>());
        Assert.Null(host.Services.GetService<IExchangeEventProcessor>());
        Assert.Equal(BackgroundServiceExceptionBehavior.StopHost,
            host.Services.GetRequiredService<IOptions<HostOptions>>().Value.BackgroundServiceExceptionBehavior);
        var source = host.Services.GetRequiredService<NpgsqlDataSource>();
        var settings = new NpgsqlConnectionStringBuilder(source.ConnectionString);
        Assert.False(settings.IncludeErrorDetail);
        Assert.False(settings.LogParameters);
        Assert.Equal("public", settings.SearchPath);
        Assert.Equal(8, settings.MaxPoolSize);
    }

    [Fact]
    public void Composition_RejectsAnAlreadyRegisteredLegacyWorker()
    {
        var builder = Builder(Configuration());
        builder.Services.AddHostedService<ExchangeEventWorker>();
        Assert.Throws<InvalidOperationException>(() => DeribitCaptureStartup.TryConfigure(builder));
    }

    [Fact]
    public void MalformedDatabaseConfiguration_IsSanitized_AndNeverFallsBackToLegacyDatabase()
    {
        var data = Configuration();
        data["ConnectionStrings:TradeOpsCaptureDb"] = "Password=synthetic-secret;unrecognized-key=value";
        data["ConnectionStrings:TradeOpsDb"] = "Host=localhost;Database=legacy;Username=owner";
        var error = Assert.Throws<InvalidOperationException>(() => DeribitCaptureStartup.TryConfigure(Builder(data)));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic-secret", error.ToString());
    }

    internal static HostApplicationBuilder Builder(Dictionary<string, string?> data)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            { DisableDefaults = true, ContentRootPath = Path.GetTempPath() });
        builder.Configuration.AddInMemoryCollection(data);
        builder.Logging.ClearProviders();
        return builder;
    }

    internal static Dictionary<string, string?> Configuration(string? connection = null) => new()
    {
        ["Worker:Mode"] = DeribitCaptureStartup.Mode,
        ["DeribitTerminal:ExecutionEnabled"] = "false", ["DeribitTerminal:MainnetExecutionEnabled"] = "false",
        ["ConnectionStrings:TradeOpsCaptureDb"] = connection ?? "Host=127.0.0.1;Database=synthetic;Username=synthetic;Password=synthetic-secret",
        ["DeribitCapture:Tenant"] = "tenant", ["DeribitCapture:Account"] = "account",
        ["DeribitCapture:Environment"] = "Testnet", ["DeribitCapture:ExpectedAccountId"] = "123",
        ["DeribitCapture:ExpectedAccountType"] = "main", ["DeribitCapture:ClientId"] = "synthetic-client",
        ["DeribitCapture:ClientSecret"] = "synthetic-secret", ["DeribitCapture:Instruments:0:Name"] = "BTC-PERPETUAL",
        ["DeribitCapture:Instruments:0:Kind"] = "future", ["DeribitCapture:Instruments:0:Currency"] = "BTC",
        ["DeribitCapture:Instruments:0:AmountUnit"] = "Usd", ["DeribitCapture:Instruments:0:AmountCurrency"] = "USD"
    };
}
