using System.Globalization;
using System.Net;
using Npgsql;
using TradeOps.Application.Interfaces;
using TradeOps.Connectors.Deribit;

namespace TradeOps.Worker;

public sealed class DeribitRecoverySources(NpgsqlDataSource capture, NpgsqlDataSource recovery) : IDisposable
{
    public NpgsqlDataSource Capture { get; } = capture;
    public NpgsqlDataSource Recovery { get; } = recovery;
    public void Dispose() { Capture.Dispose(); Recovery.Dispose(); }
}

public sealed record DeribitRecoveryHostOptions(DateTimeOffset InitialFromUtc);

/// <summary>Explicit, exclusive recovery composition. No legacy workers, exchange senders,
/// automatic migrations or grants. Capture and recovery use separate database identities.</summary>
public static class DeribitRecoveryStartup
{
    public const string Mode = "DeribitAccountRecovery";
    public static bool TryConfigure(HostApplicationBuilder builder)
    {
        if (builder.Configuration["Worker:Mode"] != Mode) return false;
        if (builder.Services.Any(d => d.ServiceType == typeof(IHostedService) ||
            d.ServiceType == typeof(IExchangeClient) || d.ServiceType == typeof(IExchangeEventStream)))
            throw new InvalidOperationException("Account recovery requires exclusive host composition.");
        var pin = DeribitCaptureStartup.ReadBinding(builder.Configuration);
        NpgsqlConnectionStringBuilder capture, recovery; DateTimeOffset from;
        try
        {
            capture = Connection("TradeOpsCaptureDb"); recovery = Connection("TradeOpsRecoveryDb");
            if (capture.Username == recovery.Username || capture.Host != recovery.Host || capture.Port != recovery.Port || capture.Database != recovery.Database ||
                !DateTimeOffset.TryParse(builder.Configuration["DeribitRecovery:InitialFromUtc"], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out from) || from.Offset != TimeSpan.Zero || from > DateTimeOffset.UtcNow.AddSeconds(-1) ||
                from < DateTimeOffset.UtcNow.AddDays(-7)) throw new ArgumentException();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        { throw new InvalidOperationException("Invalid recovery database binding or initial outage anchor; startup refused."); }
        builder.Services.AddSingleton(pin); builder.Services.AddSingleton(new DeribitRecoveryHostOptions(from));
        builder.Services.AddSingleton(_ => new DeribitRecoverySources(NpgsqlDataSource.Create(capture.ConnectionString), NpgsqlDataSource.Create(recovery.ConnectionString)));
        builder.Services.AddHttpClient("DeribitAccountRecovery", c => c.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
                UseDefaultCredentials = false, AutomaticDecompression = DecompressionMethods.None });
        builder.Services.AddSingleton<IDeribitRecoveryBindingSource>(sp => new DeribitRecoveryBindingSource(pin,
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("DeribitAccountRecovery")));
        builder.Services.AddSingleton(sp => new DeribitPrivateWebSocketStream(pin.Options,
            sp.GetRequiredService<ILogger<DeribitPrivateWebSocketStream>>()));
        builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
        builder.Services.AddSingleton<DeribitAccountRecoveryWorker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DeribitAccountRecoveryWorker>());
        return true;

        NpgsqlConnectionStringBuilder Connection(string name)
        {
            var raw = builder.Configuration.GetConnectionString(name); if (string.IsNullOrWhiteSpace(raw)) throw new ArgumentException();
            var c = new NpgsqlConnectionStringBuilder(raw) { IncludeErrorDetail = false, LogParameters = false,
                SearchPath = "public", Pooling = true, MinPoolSize = 0, MaxPoolSize = 8, Multiplexing = false, Timeout = 10, CommandTimeout = 15 };
            if (string.IsNullOrWhiteSpace(c.Host) || string.IsNullOrWhiteSpace(c.Database) || string.IsNullOrWhiteSpace(c.Username)) throw new ArgumentException();
            return c;
        }
    }
}
