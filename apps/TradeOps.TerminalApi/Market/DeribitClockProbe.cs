using System.Diagnostics;
using System.Text.Json;

namespace TradeOps.TerminalApi.Market;

/// <summary>
/// Auth-free exchange-clock sample over independent HTTPS (never the RAW L2
/// WebSocket). Cache and serialize requests so the UI does not add load to
/// the high-frequency book reader. No account IDs, API keys or private calls.
/// </summary>
public sealed class DeribitClockProbe(
    IHttpClientFactory httpClientFactory, IConfiguration configuration)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClockReading? _latest;
    private DateTimeOffset _nextRequestUtc;

    public sealed record ClockReading(
        long ClockOffsetMs, long RttMs, long SampleUnixMs);

    public static ClockReading Estimate(
        long localSendUnixMs, long rttMs, long exchangeUnixMs)
    {
        if (rttMs < 0) throw new ArgumentOutOfRangeException(nameof(rttMs));
        // Offset positive means Windows clock is ahead of exchange clock.
        // Midpoint assumes roughly symmetrical path delays; it is an estimate.
        var midpoint = checked(localSendUnixMs + rttMs / 2);
        return new ClockReading(
            checked(midpoint - exchangeUnixMs), rttMs, midpoint);
    }

    public async Task<ClockReading?> ReadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow < _nextRequestUtc)
                return _latest;

            var environment = configuration["DeribitTerminal:Environment"] ?? "testnet";
            var host = environment switch
            {
                "mainnet" => "www.deribit.com",
                "testnet" => "test.deribit.com",
                _ => null
            };
            if (host is null)
            {
                _latest = null;
                _nextRequestUtc = DateTimeOffset.UtcNow + FailureRetryDelay;
                return null;
            }

            try
            {
                // This is a single, public unauthenticated GET against a fixed
                // allowlisted host. Never attach exchange credentials.
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

                _latest = Estimate(
                    sendUnixMs, (long)Math.Round(watch.Elapsed.TotalMilliseconds),
                    exchangeUnixMs);
                _nextRequestUtc = DateTimeOffset.UtcNow + RefreshInterval;
                return _latest;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Never publish HTTP response bodies, exception messages, or
                // sensitive request details to the localhost diagnostics.
                _latest = null;
                _nextRequestUtc = DateTimeOffset.UtcNow + FailureRetryDelay;
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
