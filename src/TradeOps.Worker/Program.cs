using Microsoft.EntityFrameworkCore;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Infrastructure.Alerts;
using TradeOps.Infrastructure.Exchange;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Repositories;
using TradeOps.Worker;

var builder = Host.CreateApplicationBuilder(args);

// No execution gateway or hosted service is composed for the administrative connection probe.
if(builder.Configuration["Worker:Mode"]==DeribitFixNontradingConnection.Mode)
{
    var result=await DeribitFixNontradingConnection.RunAsync(builder.Configuration);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    if(result.Status!="FIX_NONTRADING_CONNECTION_PASS")Environment.ExitCode=1;
    return;
}

// This branch returns before any hosted service, exchange client or execution owner is composed.
if(builder.Configuration["Worker:Mode"]==DeribitFixConfigurationCheck.Mode)
{
    var check=await DeribitFixConfigurationCheck.RunAsync(builder.Configuration);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(check));
    if(check.Status!="FIX_CONFIGURATION_CHECK_PASS")Environment.ExitCode=1;
    return;
}

if(DeribitOmsExecutionStartup.TryConfigure(builder))
{
    using var omsHost=builder.Build(); var omsWorker=omsHost.Services.GetRequiredService<DeribitOmsExecutionWorker>();
    await omsHost.RunAsync();
    var qualifier=omsHost.Services.GetServices<IHostedService>().OfType<DeribitFixQualificationWorker>().SingleOrDefault();
    if(qualifier is not null)
    {
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(qualifier.Result));
        if(qualifier.Result?.Status!="FIX_QUALIFICATION_SCENARIO_PASS")Environment.ExitCode=1;
    }
    if(omsWorker.ExecuteTask?.IsFaulted==true) Environment.ExitCode=1;
    return;
}

if (DeribitRecoveryStartup.TryConfigure(builder))
{
    using var recoveryHost = builder.Build();
    var recoveryWorker = recoveryHost.Services.GetRequiredService<DeribitAccountRecoveryWorker>();
    await recoveryHost.RunAsync();
    if (recoveryWorker.ExecuteTask?.IsFaulted == true) Environment.ExitCode = 1;
    return;
}

if (DeribitCaptureStartup.TryConfigure(builder))
{
    using var captureHost = builder.Build();
    var captureWorker = captureHost.Services.GetServices<IHostedService>()
        .OfType<DeribitReadOnlyCaptureWorker>().Single();
    await captureHost.RunAsync();
    if (captureWorker.ExecuteTask?.IsFaulted == true) Environment.ExitCode = 1;
    return;
}

var connectionString = builder.Configuration.GetConnectionString("TradeOpsDb")
    ?? throw new InvalidOperationException("Connection string 'TradeOpsDb' is not configured.");

builder.Services.AddDbContext<TradeOpsDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.SectionName));

// TO-V2-18: isolated, disabled-by-default synthetic runtime canary.
// IMPORTANT: this is NOT the future PostgreSQL-backed shadow comparator.
// Existing reconciliation, orders, exchange events and Worker services
// remain untouched. Do not register on any real provider or Paper IBKR.
var syntheticShadowEnabled = builder.Configuration.GetValue<bool>(
    ReconciliationShadowStartupPolicy.SectionName + ":Enabled");
ReconciliationShadowStartupPolicy.DemandSyntheticOnly(
    syntheticShadowEnabled,
    builder.Configuration["Exchange:Provider"],
    Environment.GetEnvironmentVariable(
        ReconciliationShadowStartupPolicy.EnvironmentConfirm));

if (syntheticShadowEnabled)
{
    builder.Services.AddSingleton<IBrokerReconciliationDecisionEngine,
        BrokerReconciliationDecisionEngine>();
    builder.Services.AddHostedService<ReconciliationShadowCanaryHostedService>();
}

builder.Services.AddOptions<TradingViewDeliveryHealthSettings>()
    .Bind(builder.Configuration.GetSection(TradingViewDeliveryHealthSettings.SectionName))
    .Validate(
        value => !value.Enabled || value.EvaluationIntervalSeconds is >= 15 and <= 3600,
        "TradingView health evaluation interval must be between 15 and 3600 seconds.")
    .Validate(
        value => !value.Enabled || value.LookbackMinutes is >= 1 and <= 1440,
        "TradingView health lookback must be between 1 and 1440 minutes.")
    .Validate(
        value => !value.Enabled || value.MinimumDeliveries is >= 1 and <= 10000,
        "TradingView health minimum deliveries must be between 1 and 10000.")
    .Validate(
        value => !value.Enabled || value.CriticalFailureCount is >= 1 and <= 10000,
        "TradingView health critical failure count must be between 1 and 10000.")
    .Validate(
        value => !value.Enabled
            || (value.CriticalFailureRatePercent > 0m && value.CriticalFailureRatePercent <= 100m),
        "TradingView health critical failure rate must be greater than 0 and no more than 100.")
    .Validate(
        value => !value.Enabled
            || (value.DegradedConflictRatePercent > 0m && value.DegradedConflictRatePercent <= 100m),
        "TradingView health conflict rate must be greater than 0 and no more than 100.")
    .Validate(
        value => !value.Enabled
            || (value.DegradedRiskRejectedRatePercent > 0m && value.DegradedRiskRejectedRatePercent <= 100m),
        "TradingView health risk-rejected rate must be greater than 0 and no more than 100.")
    .Validate(
        value => !value.Enabled || value.MaxAverageLatencyMilliseconds is >= 1 and <= 60000,
        "TradingView health latency threshold must be between 1 and 60000 ms.")
    .Validate(
        value => !value.Enabled || value.NoSuccessfulDeliveryMinutes is >= 0 and <= 10080,
        "TradingView health no-success threshold must be between 0 and 10080 minutes.")
    .ValidateOnStart();

builder.Services.AddSingleton(
    serviceProvider => serviceProvider
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<TradingViewDeliveryHealthSettings>>()
        .Value);

builder.Services.AddHttpClient("telegram");
builder.Services.AddSingleton<IAlertService, TelegramAlertService>();

builder.Services.AddTradeOpsExchange(builder.Configuration);

builder.Services.AddScoped<IOrderRepository, EfOrderRepository>();
builder.Services.AddScoped<IFillRepository, EfFillRepository>();
builder.Services.AddScoped<IPositionSnapshotRepository, EfPositionSnapshotRepository>();
builder.Services.AddScoped<IRiskEventRepository, EfRiskEventRepository>();
builder.Services.AddScoped<IOperationalRunStatusRepository, EfOperationalRunStatusRepository>();
builder.Services.AddScoped<ITradingViewDeliveryAuditRepository, EfTradingViewDeliveryAuditRepository>();
builder.Services.AddScoped<ITradingViewDeliveryHealthStateRepository, EfTradingViewDeliveryHealthStateRepository>();
builder.Services.AddScoped<ITradingViewDeliveryHealthEvaluator, TradingViewDeliveryHealthEvaluator>();
builder.Services.AddSingleton<IOrderStateMachine, OrderStateMachine>();
builder.Services.AddScoped<IOrderReconciliationService, OrderReconciliationService>();
builder.Services.AddScoped<IExchangeEventProcessor, ExchangeEventProcessor>();
builder.Services.AddScoped<IPositionService, PositionService>();
builder.Services.AddScoped<IPositionReconciliationService, PositionReconciliationService>();

builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<ExchangeEventWorker>();
builder.Services.AddHostedService<TradingViewDeliveryHealthWorker>();

var host = builder.Build();
host.Run();
