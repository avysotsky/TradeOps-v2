using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TradeOps.Connectors.Deribit;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitPrivateSocketTests
{
    [Theory]
    [InlineData("oversized")]
    [InlineData("binary")]
    [InlineData("utf8")]
    public async Task RealLoopbackWebSocket_RejectsOversizedBinaryAndInvalidUtf8(string mode)
    {
        using var host = Create(async socket =>
        {
            if (mode == "oversized")
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes("1234"), WebSocketMessageType.Text, false, CancellationToken.None);
                await socket.SendAsync(Encoding.UTF8.GetBytes("5"), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            else await socket.SendAsync(mode == "utf8" ? new byte[] { 0xff } : new byte[] { 1 },
                mode == "utf8" ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, CancellationToken.None);
        });
        await host.StartAsync();
        try
        {
            using var socket = new DeribitPrivateSocket();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await socket.ConnectAsync(Address(host), stop.Token);
            var failure = await Record.ExceptionAsync(() => socket.ReceiveAsync(4, stop.Token));
            // ManagedWebSocket itself can reject invalid UTF-8 before the wrapper decoder.
            Assert.True(failure is DeribitCaptureException or WebSocketException);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task RealLoopbackWebSocket_ReassemblesSplitUtf8WithoutChangingText()
    {
        using var host = Create(async socket =>
        {
            var bytes = Encoding.UTF8.GetBytes("🙂");
            await socket.SendAsync(bytes.AsMemory(0, 2), WebSocketMessageType.Text, false, CancellationToken.None);
            await socket.SendAsync(bytes.AsMemory(2), WebSocketMessageType.Text, true, CancellationToken.None);
        });
        await host.StartAsync();
        try
        {
            using var socket = new DeribitPrivateSocket();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await socket.ConnectAsync(Address(host), stop.Token);
            Assert.Equal("🙂", await socket.ReceiveAsync(4, stop.Token));
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task RealLoopbackWebSocket_CancellationEndsPendingReceive()
    {
        using var host = Create(async socket =>
        {
            try { await socket.ReceiveAsync(new byte[8], CancellationToken.None); }
            catch (WebSocketException) { }
        });
        await host.StartAsync();
        try
        {
            using var socket = new DeribitPrivateSocket();
            using var stop = new CancellationTokenSource();
            await socket.ConnectAsync(Address(host), CancellationToken.None);
            var pending = socket.ReceiveAsync(4, stop.Token);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(8)));
        }
        finally { await host.StopAsync(); }
    }

    private static WebApplication Create(Func<WebSocket, Task> send)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            { Args = Array.Empty<string>(), ContentRootPath = Path.GetTempPath(), EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await send(socket);
        });
        return app;
    }
    private static Uri Address(WebApplication host) => new(host.Services.GetRequiredService<IServer>().Features
        .Get<IServerAddressesFeature>()!.Addresses.Single().Replace("http://", "ws://", StringComparison.Ordinal));
}
