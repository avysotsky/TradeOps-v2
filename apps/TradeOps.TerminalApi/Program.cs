using TradeOps.TerminalApi;
using TradeOps.TerminalApi.Market;

// Independent Deribit manual terminal API: intentionally NO TradeOpsDb,
// migrations, worker, signal routes, webhook, other exchange or trading DI.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSingleton<DeribitRawFeedHub>();
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
