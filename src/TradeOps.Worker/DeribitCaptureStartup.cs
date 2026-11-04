using System.Globalization;
using Npgsql;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using TradeOps.Domain.Oms;

namespace TradeOps.Worker;

/// <summary>Exclusive opt-in composition. No legacy workers, migrations or exchange senders.</summary>
public static class DeribitCaptureStartup
{
    public const string Mode = "DeribitReadOnlyCapture";
    private const string Prefix = "DeribitCapture:";

    public static bool IsSelected(IConfiguration configuration) => configuration["Worker:Mode"] switch
    {
        null or "Legacy" => false,
        Mode => true,
        _ => throw new InvalidOperationException("Unknown Worker mode; startup refused.")
    };

    public static bool TryConfigure(HostApplicationBuilder builder)
    {
        if (!IsSelected(builder.Configuration)) return false;
        if (builder.Services.Any(d => d.ServiceType == typeof(IHostedService) ||
            d.ServiceType == typeof(IExchangeClient) || d.ServiceType == typeof(IExchangeEventStream)))
            throw new InvalidOperationException("Read-only capture requires exclusive host composition.");
        var binding = ReadBinding(builder.Configuration);
        string connection;
        try
        {
            var raw = builder.Configuration.GetConnectionString("TradeOpsCaptureDb");
            if (string.IsNullOrWhiteSpace(raw)) throw new ArgumentException();
            var settings = new NpgsqlConnectionStringBuilder(raw)
            {
                IncludeErrorDetail = false, LogParameters = false, SearchPath = "public",
                Pooling = true, MinPoolSize = 0, MaxPoolSize = 8, Multiplexing = false,
                Timeout = 10, CommandTimeout = 15
            };
            if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Database) ||
                string.IsNullOrWhiteSpace(settings.Username)) throw new ArgumentException();
            connection = settings.ConnectionString;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        { throw new InvalidOperationException("Invalid capture database configuration; startup refused."); }

        builder.Services.AddSingleton(binding);
        builder.Services.Configure<HostOptions>(options =>
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
        builder.Services.AddSingleton(sp => new DeribitPrivateWebSocketStream(binding.Options,
            sp.GetRequiredService<ILogger<DeribitPrivateWebSocketStream>>()));
        builder.Services.AddHostedService<DeribitReadOnlyCaptureWorker>();
        return true;
    }

    public static DeribitPrivateCaptureBinding ReadBinding(IConfiguration configuration)
    {
        try
        {
            foreach (var key in new[] { "DeribitTerminal:ExecutionEnabled", "DeribitTerminal:MainnetExecutionEnabled" })
                if (!bool.TryParse(configuration[key], out var enabled) || enabled) throw new ArgumentException();
            var environment = Required("Environment") switch
            {
                "Testnet" => OmsEnvironment.Testnet,
                "Mainnet" => OmsEnvironment.Mainnet,
                _ => throw new ArgumentException()
            };
            var subaccount = configuration[Prefix + "Subaccount"];
            var scope = new AccountKey(new(Required("Tenant")), new("DERIBIT"), environment,
                new(Required("Account")), subaccount is null ? null : new TradingSubaccountId(subaccount));
            if (!long.TryParse(Required("ExpectedAccountId"), NumberStyles.None, CultureInfo.InvariantCulture, out var accountId))
                throw new ArgumentException();
            var isSubaccount = Required("ExpectedAccountType") switch
            { "main" => false, "subaccount" => true, _ => throw new ArgumentException() };
            var instruments = configuration.GetSection(Prefix + "Instruments").GetChildren().Take(4097).Select(section =>
            {
                string Field(string key) => section[key] ?? throw new ArgumentException();
                return new DeribitPrivateInstrument(Field("Name"), Field("Kind"), Field("Currency"),
                    Field("AmountUnit") switch { "Usd" => OmsPrivateAmountUnit.Usd,
                        "BaseCoin" => OmsPrivateAmountUnit.BaseCoin, _ => throw new ArgumentException() }, Field("AmountCurrency"));
            }).ToArray();
            var options = new DeribitOptions
            {
                WebSocketUrl = environment == OmsEnvironment.Testnet ? "wss://test.deribit.com/ws/api/v2" : "wss://www.deribit.com/ws/api/v2",
                ClientId = Required("ClientId"), ClientSecret = Required("ClientSecret")
            };
            return new(scope, options, new(accountId, isSubaccount, instruments));

            string Required(string key) => configuration[Prefix + key] is { } value && !string.IsNullOrWhiteSpace(value)
                ? value : throw new ArgumentException();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or OverflowException)
        { throw new InvalidOperationException("Invalid Deribit read-only capture binding; startup refused."); }
    }
}
