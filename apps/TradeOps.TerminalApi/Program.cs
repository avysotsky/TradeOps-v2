using TradeOps.TerminalApi;
using TradeOps.TerminalApi.Market;

namespace TradeOps.TerminalApi;

// Explicit, uniquely named executable entry point. Both TradeOps.Api and
// this standalone ASP.NET Core application are referenced by shared xUnit
// projects. A second compiler-generated global Program would make all
// WebApplicationFactory<Program> references ambiguous (CS0433).
internal static class TerminalApiEntryPoint
{
    public static void Main(string[] args)
    {
        // Independent Deribit manual terminal API: intentionally NO TradeOpsDb,
        // migrations, worker, signal routes, webhook, other exchange or trading DI.
        var builder = WebApplication.CreateBuilder(args);
        
        builder.Services.AddControllers();
        builder.Services.AddSingleton<DeribitRawFeedHub>();
        builder.Services.AddSingleton<DeribitClockProbe>();
        builder.Services.AddHttpClient("deribit-terminal", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(12);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false
        });
        
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions {
            KeepAliveInterval = TimeSpan.FromSeconds(15)
        });
        app.MapControllers();
        app.MapRawMarketStream();
        // Binding fixed to loopback, independent of appsettings/launch profiles.
        app.Run("http://127.0.0.1:5058");
    }
}
