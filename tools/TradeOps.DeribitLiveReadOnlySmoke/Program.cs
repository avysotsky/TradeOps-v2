using System.Net;
using TradeOps.Infrastructure.Exchange.Deribit;

// Explicit one-time opt-in. This program is never launched from the API,
// Worker, a CI workflow or Docker acceptance. Nothing mutates account state.
const string permission = "READ_ONLY_MAINNET_THREE_CALLS";
var consent = Environment.GetEnvironmentVariable(
    "TRADEOPS_DERIBIT_LIVE_CONFIRM");
var key = Environment.GetEnvironmentVariable(
    "DERIBIT_LIVE_READONLY_CLIENT_ID");
var secret = Environment.GetEnvironmentVariable(
    "DERIBIT_LIVE_READONLY_CLIENT_SECRET");

if (consent != permission
    || string.IsNullOrWhiteSpace(key)
    || string.IsNullOrWhiteSpace(secret))
{
    Console.Error.WriteLine(
        "DERIBIT LIVE READONLY NOT RUN: opt-in and dedicated read-only key required.");
    return 2;
}

try
{
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
    var client = new DeribitLiveReadOnlyClient(http, key, secret);
    var counts = await client.ProbeAsync();

    Console.WriteLine(
        $"TRADEOPS DERIBIT LIVE READONLY PASS account_currencies={counts.AccountCurrencies} positions={counts.Positions} open_orders={counts.OpenOrders} futures={counts.Futures} options={counts.Options} future_combos={counts.FutureCombos} option_combos={counts.OptionCombos} mutating_calls=0");
    return 0;
}
catch (Exception error)
{
    // No server body, URL, account data, credentials or exception message.
    Console.Error.WriteLine(
        $"DERIBIT LIVE READONLY FAIL type={error.GetType().Name} mutating_calls=0");
    return 1;
}
