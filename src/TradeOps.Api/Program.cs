using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using TradeOps.Api.Contracts;
using TradeOps.Api.Integrations.TradingView;
using TradeOps.Api.Security;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Infrastructure.Alerts;
using TradeOps.Infrastructure.Exchange;
using TradeOps.Infrastructure.Persistence;
using TradeOps.Infrastructure.Persistence.Repositories;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOptions<SignalIngressAuthOptions>()
    .Bind(builder.Configuration.GetSection(SignalIngressAuthOptions.SectionName))
    .Validate(
        options => !options.Enabled || !string.IsNullOrWhiteSpace(options.HeaderName),
        "Signal ingress authentication header name is required when authentication is enabled.")
    .Validate(
        options => !options.Enabled || !string.IsNullOrWhiteSpace(options.ApiKey),
        "Signal ingress API key is required when authentication is enabled.")
    .ValidateOnStart();
builder.Services.AddOptions<SignalIngressReplayProtectionOptions>()
    .Bind(builder.Configuration.GetSection(SignalIngressReplayProtectionOptions.SectionName))
    .Validate(
        options => !options.Enabled
            || builder.Configuration.GetValue<bool>($"{SignalIngressAuthOptions.SectionName}:Enabled"),
        "Signal ingress replay protection requires signal ingress authentication to be enabled.")
    .Validate(
        options => !options.Enabled
            || (!string.IsNullOrWhiteSpace(options.TimestampHeaderName)
                && !string.IsNullOrWhiteSpace(options.RequestIdHeaderName)),
        "Signal ingress replay protection header names are required when replay protection is enabled.")
    .Validate(
        options => !options.Enabled
            || !string.Equals(
                options.TimestampHeaderName,
                options.RequestIdHeaderName,
                StringComparison.OrdinalIgnoreCase),
        "Signal ingress replay protection timestamp and request-id header names must be different.")
    .Validate(
        options => !options.Enabled
            || (options.AllowedClockSkewSeconds is >= 1 and <= 3600),
        "Signal ingress replay protection clock skew must be between 1 and 3600 seconds.")
    .Validate(
        options => !options.Enabled
            || (options.ReceiptRetentionSeconds >= options.AllowedClockSkewSeconds * 2
                && options.ReceiptRetentionSeconds <= 86400),
        "Signal ingress replay receipt retention must be at least twice the clock-skew window and no more than 86400 seconds.")
    .ValidateOnStart();
builder.Services.AddOptions<SignalIngressSigningOptions>()
    .Bind(builder.Configuration.GetSection(SignalIngressSigningOptions.SectionName))
    .Validate(
        options => !options.Enabled
            || builder.Configuration.GetValue<bool>($"{SignalIngressAuthOptions.SectionName}:Enabled"),
        "Signal ingress signing requires signal ingress authentication to be enabled.")
    .Validate(
        options => !options.Enabled
            || builder.Configuration.GetValue<bool>($"{SignalIngressReplayProtectionOptions.SectionName}:Enabled"),
        "Signal ingress signing requires replay protection to be enabled.")
    .Validate(
        options => !options.Enabled
            || !string.IsNullOrWhiteSpace(options.SignatureHeaderName),
        "Signal ingress signature header name is required when signing is enabled.")
    .Validate(
        options => !options.Enabled
            || options.Secret.Length >= 32,
        "Signal ingress signing secret must contain at least 32 characters when signing is enabled.")
    .Validate(
        options => !options.Enabled
            || (options.MaxBodyBytes is >= 1024 and <= 1048576),
        "Signal ingress signed body limit must be between 1024 and 1048576 bytes.")
    .Validate(
        options => !options.Enabled
            || !string.Equals(
                options.SignatureHeaderName,
                builder.Configuration[$"{SignalIngressAuthOptions.SectionName}:HeaderName"],
                StringComparison.OrdinalIgnoreCase),
        "Signal ingress signature header must be different from the API-key header.")
    .Validate(
        options => !options.Enabled
            || (!string.Equals(
                    options.SignatureHeaderName,
                    builder.Configuration[$"{SignalIngressReplayProtectionOptions.SectionName}:TimestampHeaderName"],
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    options.SignatureHeaderName,
                    builder.Configuration[$"{SignalIngressReplayProtectionOptions.SectionName}:RequestIdHeaderName"],
                    StringComparison.OrdinalIgnoreCase)),
        "Signal ingress signature header must be different from replay-protection headers.")
    .ValidateOnStart();
builder.Services.AddOptions<TradingViewWebhookOptions>()
    .Bind(builder.Configuration.GetSection(TradingViewWebhookOptions.SectionName))
    .Validate(
        options => !options.Enabled || !string.IsNullOrWhiteSpace(options.GatewayHeaderName),
        "TradingView gateway header name is required when the adapter is enabled.")
    .Validate(
        options => !options.Enabled || options.GatewayKey.Length >= 32,
        "TradingView gateway key must contain at least 32 characters when the adapter is enabled.")
    .Validate(
        options => !options.Enabled
            || !options.RequireGatewayIpAllowlist
            || options.AllowedGatewayIps.Length > 0,
        "TradingView gateway IP allowlist must not be empty when IP validation is enabled.")
    .Validate(
        options => !options.Enabled
            || !options.RequireGatewayIpAllowlist
            || options.AllowedGatewayIps.All(value => System.Net.IPAddress.TryParse(value, out _)),
        "TradingView gateway IP allowlist contains an invalid IP address.")
    .ValidateOnStart();
builder.Services.AddOptions<OperatorApiAuthOptions>()
    .Bind(builder.Configuration.GetSection(OperatorApiAuthOptions.SectionName))
    .Validate(
        options => !options.Enabled || !string.IsNullOrWhiteSpace(options.HeaderName),
        "Operator API authentication header name is required when authentication is enabled.")
    .Validate(
        options => !options.Enabled || !string.IsNullOrWhiteSpace(options.ApiKey),
        "Operator API key is required when authentication is enabled.")
    .ValidateOnStart();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "TradeOps API",
        Version = "v1",
        Description = "Execution and operator API. Exchange routes reflect the configured exchange adapter; operator audit routes reflect local PostgreSQL state."
    });
});

var connectionString = builder.Configuration.GetConnectionString("TradeOpsDb")
    ?? throw new InvalidOperationException("Connection string 'TradeOpsDb' is not configured.");

builder.Services.AddDbContext<TradeOpsDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
builder.Services.AddHttpClient("telegram");
builder.Services.AddSingleton<IAlertService, TelegramAlertService>();
builder.Services.AddTradeOpsExchange(builder.Configuration);

builder.Services.AddScoped<IOrderRepository, EfOrderRepository>();
builder.Services.AddScoped<IOrderCancellationCandidateRepository, EfOrderCancellationCandidateRepository>();
builder.Services.AddScoped<IFillRepository, EfFillRepository>();
builder.Services.AddScoped<IPositionSnapshotRepository, EfPositionSnapshotRepository>();
builder.Services.AddScoped<IRiskEventRepository, EfRiskEventRepository>();
builder.Services.AddScoped<IOperationalRiskStateRepository, EfOperationalRiskStateRepository>();
builder.Services.AddScoped<IOperationalRunStatusRepository, EfOperationalRunStatusRepository>();
builder.Services.AddScoped<IOperationalRunHistoryRepository, EfOperationalRunHistoryRepository>();
builder.Services.AddScoped<ITradingSignalRepository, EfTradingSignalRepository>();
builder.Services.AddScoped<ITradingSignalOutcomeHistoryRepository, EfTradingSignalOutcomeHistoryRepository>();
builder.Services.AddScoped<IOperatorReadRepository, EfOperatorReadRepository>();
builder.Services.AddScoped<ILocalOrderAuditRepository, EfLocalOrderAuditRepository>();
builder.Services.AddScoped<IExecutionMetricsRepository, EfExecutionMetricsRepository>();
builder.Services.AddScoped<IExecutionMetricsBySymbolRepository, EfExecutionMetricsBySymbolRepository>();
builder.Services.AddScoped<ISignalTransitionMetricsRepository, EfSignalTransitionMetricsRepository>();
builder.Services.AddScoped<ISignalIngressReplayStore, EfSignalIngressReplayStore>();
builder.Services.AddScoped<ISignalIngressRequestAuditRepository, EfSignalIngressRequestAuditRepository>();
builder.Services.AddScoped<ITradingViewDeliveryAuditRepository, EfTradingViewDeliveryAuditRepository>();
builder.Services.AddScoped<ITradingViewDeliveryHealthStateRepository, EfTradingViewDeliveryHealthStateRepository>();
builder.Services.AddSingleton<IClientOrderIdGenerator, ClientOrderIdGenerator>();
builder.Services.AddSingleton<IOrderStateMachine, OrderStateMachine>();
builder.Services.AddSingleton(new RiskSettings());
builder.Services.AddSingleton(new AccountingSettings
{
    SettlementCurrency = builder.Configuration["Accounting:SettlementCurrency"]?.Trim().ToUpperInvariant() ?? "USDT"
});
builder.Services.AddScoped<IRiskControlService, RiskControlService>();
builder.Services.AddScoped<IRiskEngine, RiskEngine>();
builder.Services.AddScoped<IOrderManager, OrderManager>();
builder.Services.AddScoped<IOrderCancellationService, OrderCancellationService>();
builder.Services.AddScoped<IOrderBulkCancellationService, OrderBulkCancellationService>();
builder.Services.AddScoped<IEmergencyStopService, EmergencyStopService>();
builder.Services.AddScoped<ISignalExecutionService, SignalExecutionService>();
builder.Services.AddScoped<IOrderReconciliationService, OrderReconciliationService>();
builder.Services.AddScoped<IPositionService, PositionService>();
builder.Services.AddScoped<IPositionReconciliationService, PositionReconciliationService>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<TradeOpsDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.UseMiddleware<SignalIngressAuthenticationMiddleware>();

app.UseSwagger();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "TradeOps API v1"));

app.MapGet("/health", () => Results.Ok(new HealthResponse("live"))).ExcludeFromDescription();
app.MapGet("/health/live", () => Results.Ok(new HealthResponse("live")))
    .WithName("GetLiveness")
    .WithTags("Health")
    .Produces<HealthResponse>(StatusCodes.Status200OK);

app.MapGet("/health/ready", async (
        TradeOpsDbContext dbContext,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken) =>
    {
        var dependencies = new Dictionary<string, string>();
        bool postgresReady;

        try
        {
            postgresReady = await dbContext.Database.CanConnectAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            postgresReady = false;
        }

        dependencies["postgres"] = postgresReady ? "ready" : "unavailable";
        if (!postgresReady)
        {
            return Results.Json(
                new HealthResponse("not-ready", dependencies),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            var exchangeClient = serviceProvider.GetService<IExchangeClient>();
            if (exchangeClient is null)
            {
                dependencies["exchangeClient"] = "unavailable";
                return Results.Json(
                    new HealthResponse("not-ready", dependencies),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            dependencies["exchangeClient"] = exchangeClient.GetType().Name;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            dependencies["exchangeClient"] = "unavailable";
            return Results.Json(
                new HealthResponse("not-ready", dependencies),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Json(
            new HealthResponse("ready", dependencies),
            statusCode: StatusCodes.Status200OK);
    })
    .WithName("GetReadiness")
    .WithTags("Health")
    .Produces<HealthResponse>(StatusCodes.Status200OK)
    .Produces<HealthResponse>(StatusCodes.Status503ServiceUnavailable);

app.MapControllers();
app.Run();

public partial class Program
{
}
