using System.Net;
using TradeOps.Infrastructure.Exchange.Deribit;

// Owner opt-in: no trading, cancellation, editing, wallet or transfer methods.
if (Environment.GetEnvironmentVariable("TRADEOPS_DERIBIT_LIVE_HISTORY_CONFIRM")
    != "READ_ONLY_MAINNET_HISTORY_TWO_MODES"
    || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
        "DERIBIT_LIVE_READONLY_CLIENT_ID"))
    || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
        "DERIBIT_LIVE_READONLY_CLIENT_SECRET")))
{
    Console.Error.WriteLine(
        "DERIBIT HISTORY READONLY NOT RUN: owner opt-in and local read-only key required");
    return 2;
}

var currency = args.Length >= 1 ? args[0] : "BTC";
var daysString = args.Length >= 2 ? args[1] : "7";
if (args.Length > 2 || !int.TryParse(daysString, out var days)
    || days is < 1 or > 7)
{
    Console.Error.WriteLine("DERIBIT HISTORY READONLY NOT RUN: days must be 1..7");
    return 2;
}

try
{
    var endMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var startMs = endMs - days * 24L * 60 * 60 * 1000;
    DeribitTradeHistoryCollector.ValidateScope(currency, startMs, endMs);

    using var handler = new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false
    };
    using var http = new HttpClient(handler)
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    var client = new DeribitLiveReadOnlyClient(
        http,
        Environment.GetEnvironmentVariable("DERIBIT_LIVE_READONLY_CLIENT_ID")!,
        Environment.GetEnvironmentVariable("DERIBIT_LIVE_READONLY_CLIENT_SECRET")!);

    // Two separate Deribit indexes: historical=true excludes recent
    // data during asynchronous indexing; never claim global completeness.
    var archived = await client.CollectTradeHistoryAsync(
        currency, startMs, endMs, historical: true);
    var recent = await client.CollectTradeHistoryAsync(
        currency, startMs, endMs, historical: false);

    Console.WriteLine(
        $"DERIBIT HISTORY READONLY PASS currency={currency} days={days}"
        + $" historical_trades={archived.UniqueTrades}"
        + $" recent_trades={recent.UniqueTrades}"
        + $" historical_pages={archived.PageCalls}"
        + $" recent_pages={recent.PageCalls}"
        + $" duplicates_boundary={archived.DuplicateBoundaryTrades + recent.DuplicateBoundaryTrades}"
        + " pages_exhausted=true history_complete=false indexing_gap_possible=true"
        + " mutating_calls=0");
    return 0;
}
catch (Exception error)
{
    // Never print broker data, raw responses, untrusted URLs or private IDs.
    Console.Error.WriteLine(
        $"DERIBIT HISTORY READONLY FAIL type={error.GetType().Name} mutating_calls=0");
    return 1;
}
