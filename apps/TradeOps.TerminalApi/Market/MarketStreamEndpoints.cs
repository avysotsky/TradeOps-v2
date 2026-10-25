using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using TradeOps.TerminalApi.Market;

namespace TradeOps.TerminalApi;

/// <summary>
/// Strictly localhost read-only WebSocket fanout. Only the Vite server-side
/// WebSocket proxy adds the short-lived operator header. Browser JS never
/// receives the secret. No POST, account keys, order placement or DB writes.
/// </summary>
public static class MarketStreamEndpoints
{
    public static IEndpointRouteBuilder MapRawMarketStream(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/deribit-terminal/market/stream", async (
            HttpContext ctx, DeribitRawFeedHub hub, IConfiguration cfg) =>
        {
            if (!Authorized(ctx, cfg))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            // A WebSocket from another site (even on this workstation) is
            // not eligible for Vite auto authorization.
            if (ctx.Request.Headers["Origin"].ToString() != "http://127.0.0.1:5173")
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var symbol = ctx.Request.Query["instrument"].ToString();
            if (!ValidInstrument(symbol))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var depthValue = ctx.Request.Query["depth"].ToString();
            if (!DeribitBookSubscription.TryParseMode(depthValue, out var depth))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            DeribitRawFeedHub.StreamLease lease;
            try { lease = hub.Subscribe(symbol, depth); }
            catch (InvalidOperationException)
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            using (lease)
            using (var browser = await ctx.WebSockets.AcceptWebSocketAsync())
            {
                try
                {
                    await foreach (var json in lease.Reader.ReadAllAsync(ctx.RequestAborted))
                    {
                        if (browser.State != WebSocketState.Open) break;
                        var bytes = Encoding.UTF8.GetBytes(json);
                        await browser.SendAsync(bytes, WebSocketMessageType.Text,
                            true, ctx.RequestAborted);
                    }
                }
                catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
                catch (WebSocketException) { }
            }
        });
        // Operator-only public clock probe. No exchange key or private API
        // is transmitted; errors remain sanitized and never affect RAW L2.
        app.MapGet("/api/deribit-terminal/market/clock", async (
            HttpContext ctx, DeribitClockProbe probe, IConfiguration cfg) =>
        {
            if (!Authorized(ctx, cfg))
                return Results.Unauthorized();

            var reading = await probe.ReadAsync(ctx.RequestAborted);
            if (reading is null)
                return Results.Json(new { status = "unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            return Results.Ok(new {
                status = "ok",
                clockOffsetMs = reading.ClockOffsetMs,
                rttMs = reading.RttMs,
                sampleUnixMs = reading.SampleUnixMs
            });
        });
        return app;
    }

    private static bool ValidInstrument(string value)
    {
        if (value.Length is < 6 or > 85 ||
            !(value.StartsWith("BTC-") || value.StartsWith("ETH-") ||
              value.StartsWith("BTC_") || value.StartsWith("ETH_")))
            return false;
        foreach (var c in value)
            if (!(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))
                return false;
        return true;
    }

    private static bool Authorized(HttpContext ctx, IConfiguration cfg)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(
            remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
            return false;
        var expected = cfg["DeribitTerminal:OperatorKey"];
        if (string.IsNullOrWhiteSpace(expected) || expected.Length < 32 ||
            !ctx.Request.Headers.TryGetValue("X-TradeOps-Terminal-Key", out var provided) ||
            provided.Count != 1 || string.IsNullOrWhiteSpace(provided[0]))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(provided[0]!)));
    }
}
