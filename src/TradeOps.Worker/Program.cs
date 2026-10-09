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

var connectionString = builder.Configuration.GetConnectionString("TradeOpsDb")
    ?? throw new InvalidOperationException("Connection string 'TradeOpsDb' is not configured.");

builder.Services.AddDbContext<TradeOpsDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.SectionName));

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
