using System.Diagnostics;
using System.Text.Json;

namespace TradeOps.TerminalApi.Market;

/// <summary>
/// Explicit operator-requested exchange clock check over public HTTPS,
/// independent of the RAW L2 WebSocket. No background polling, caching,
/// private credentials, account data, or exchange trading methods.
/// </summary>
public sealed class DeribitClockProbe(
    IHttpClientFactory httpClientFactory, IConfiguration configuration)
{
    // One outstanding diagnostic request per local Terminal API instance.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public sealed record ClockReading(
        long ClockOffsetMs, long RttMs, long SampleUnixMs);

    public static ClockReading Estimate(
        long localSendUnixMs, long rttMs, long exchangeUnixMs)
    {
        if (rttMs < 0) throw new ArgumentOutOfRangeException(nameof(rttMs));
        // Positive means Windows time is ahead of exchange time.
        // This midpoint estimate assumes roughly symmetric network timing.
        var midpoint = checked(localSendUnixMs + rttMs / 2);
        return new ClockReading(
            checked(midpoint - exchangeUnixMs), rttMs, midpoint);
    }

    public async Task<ClockReading?> ReadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var environment = configuration["DeribitTerminal:Environment"] ?? "testnet";
            var host = environment switch
            {
                "mainnet" => "www.deribit.com",
                "testnet" => "test.deribit.com",
                _ => null
            };
            if (host is null) return null;

            try
            {
                // A single public unauthenticated GET, triggered solely by
                // the operator button. Fixed host, no redirect/proxy/cookies,
                // no Deribit API key, separate from book stream.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var client = httpClientFactory.CreateClient("deribit-terminal");
                var sendUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var watch = Stopwatch.StartNew();
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "https://" + host + "/api/v2/public/get_time");
                using var response = await client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                using var body = await JsonDocument.ParseAsync(stream,
                    new JsonDocumentOptions { MaxDepth = 8 }, deadline.Token);
                watch.Stop();

                if (!body.RootElement.TryGetProperty("result", out var result) ||
                    result.ValueKind != JsonValueKind.Number ||
                    !result.TryGetInt64(out var exchangeUnixMs) ||
                    exchangeUnixMs is < 1_500_000_000_000 or > 4_000_000_000_000)
                    throw new JsonException("Invalid public exchange clock response.");

                return Estimate(
                    sendUnixMs, (long)Math.Round(watch.Elapsed.TotalMilliseconds),
                    exchangeUnixMs);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Never leak network/response details to localhost clients.
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
