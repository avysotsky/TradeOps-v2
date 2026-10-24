using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace TradeOps.Api.Controllers;

/// <summary>
/// Public Deribit market-data endpoints for the localhost operator React screen.
/// No Deribit API credentials are used for these requests. Not connected to
/// trade execution, persistent databases, or background workers.
/// </summary>
[ApiController]
[Route("api/deribit-terminal/market")]
public sealed class DeribitMarketController(
    IHttpClientFactory clientFactory, IConfiguration config) : ControllerBase
{
    private static readonly Regex InstrumentPattern = new(
        @"^(BTC|ETH)[-_][A-Z0-9_-]{1,72}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, InstrumentCache> InstrumentLists = new();
    private static readonly SemaphoreSlim UniverseGate = new(1, 1);
    private static DateTimeOffset LastUniverseFetch = DateTimeOffset.MinValue;

    private sealed record InstrumentCache(DateTimeOffset Expires, IReadOnlyList<MarketInstrument> Data);
    public sealed record MarketInstrument(
        string Name, string Kind, string Currency, bool Active,
        decimal? TickSize, decimal? ContractSize, decimal? MinTradeAmount,
        long? ExpirationTimestamp, decimal? Strike, string? OptionType);

    private string Host => (config["DeribitTerminal:Environment"] ?? "testnet") switch
    {
        "mainnet" => "www.deribit.com",
        "testnet" => "test.deribit.com",
        _ => throw new InvalidOperationException("Unrecognized Deribit market-data environment.")
    };

    private bool Authorized()
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(
            remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
            return false;
        var expected = config["DeribitTerminal:OperatorKey"];
        if (string.IsNullOrWhiteSpace(expected) || expected.Length < 32 ||
            !Request.Headers.TryGetValue("X-TradeOps-Terminal-Key", out var value) ||
            value.Count != 1 || string.IsNullOrWhiteSpace(value[0]))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(value[0]!)));
    }

    private IActionResult Denied() => Unauthorized(
        new { error = "Local market-data operator authorization required." });

    private static bool ValidInstrument(string? name) =>
        name is not null && InstrumentPattern.IsMatch(name);

    [HttpGet("instruments")]
    public async Task<IActionResult> Instruments(
        [FromQuery] string currency = "ETH",
        [FromQuery] string kind = "future",
        [FromQuery] string? search = null,
        [FromQuery] int limit = 80,
        CancellationToken ct = default)
    {
        if (!Authorized()) return Denied();
        if (currency is not ("BTC" or "ETH") || kind is not ("future" or "option") ||
            limit is < 1 or > 150 || search?.Length > 60 ||
            (search is not null && search.Any(c =>
                !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))))
            return BadRequest(new { error = "Invalid currency, kind, search or limit." });

        var cacheKey = Host + "|" + currency + "|" + kind;
        try
        {
            if (!InstrumentLists.TryGetValue(cacheKey, out var cached) ||
                cached.Expires <= DateTimeOffset.UtcNow)
            {
                await UniverseGate.WaitAsync(ct);
                try
                {
                    if (!InstrumentLists.TryGetValue(cacheKey, out cached) ||
                        cached.Expires <= DateTimeOffset.UtcNow)
                    {
                        // public/get_instruments is limited to about 1 req/s.
                        var wait = TimeSpan.FromMilliseconds(1100) -
                            (DateTimeOffset.UtcNow - LastUniverseFetch);
                        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                        LastUniverseFetch = DateTimeOffset.UtcNow;
                        var data = await ReadPublicAsync("public/get_instruments",
                        [
                            new("currency", currency),
                            new("kind", kind),
                            new("expired", "false")
                        ], ct);

                        if (data.ValueKind != JsonValueKind.Array)
                            throw new InvalidOperationException("Unexpected instruments response.");
                        var list = new List<MarketInstrument>();
                        foreach (var entry in data.EnumerateArray())
                        {
                            if (entry.ValueKind != JsonValueKind.Object) continue;
                            var name = Str(entry, "instrument_name");
                            if (!ValidInstrument(name)) continue;
                            if (Bool(entry, "is_active") != true) continue;
                            list.Add(new MarketInstrument(
                                name!, Str(entry, "kind") ?? kind,
                                currency, true, Num(entry, "tick_size"),
                                Num(entry, "contract_size"),
                                Num(entry, "min_trade_amount"),
                                Long(entry, "expiration_timestamp"),
                                Num(entry, "strike"), Str(entry, "option_type")));
                        }
                        // Hard cap cached parsed data; no raw JSON cache.
                        cached = new InstrumentCache(DateTimeOffset.UtcNow.AddSeconds(60),
                            list.OrderBy(x => x.ExpirationTimestamp ?? long.MaxValue)
                                .ThenBy(x => x.Name, StringComparer.Ordinal)
                                .Take(3000).ToArray());
                        InstrumentLists[cacheKey] = cached;
                    }
                }
                finally { UniverseGate.Release(); }
            }

            var query = cached!.Data.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(x =>
                    x.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
            return Ok(new {
                currency, kind, available = query.Count(),
                items = query.Take(limit).ToArray(),
                cachedUntilUtc = cached.Expires,
                fetchedFrom = Host
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return StatusCode(502, new { error = "Deribit instruments unavailable." });
        }
    }

    [HttpGet("orderbook")]
    public async Task<IActionResult> Book(
        [FromQuery] string instrument,
        [FromQuery] int depth = 10,
        CancellationToken ct = default)
    {
        if (!Authorized()) return Denied();
        if (!ValidInstrument(instrument) || depth is < 1 or > 20)
            return BadRequest(new { error = "Invalid instrument or book depth." });
        try
        {
            var r = await ReadPublicAsync("public/get_order_book", [
                new("instrument_name", instrument),
                new("depth", depth.ToString(CultureInfo.InvariantCulture))
            ], ct);
            var bids = Levels(r, "bids", depth);
            var asks = Levels(r, "asks", depth);
            return Ok(new {
                instrument,
                bids, asks,
                markPrice = Num(r, "mark_price"),
                timestamp = Long(r, "timestamp"),
                changeId = Long(r, "change_id"),
                receivedAtUtc = DateTimeOffset.UtcNow
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return StatusCode(502, new { error = "Deribit order book unavailable." });
        }
    }

    public sealed record Level(decimal Price, decimal Amount);
    private static Level[] Levels(JsonElement parent, string name, int depth)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out var rows) ||
            rows.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Invalid order-book side.");
        var result = new List<Level>();
        foreach (var row in rows.EnumerateArray().Take(depth))
        {
            // HTTP snapshot levels have [price, amount]. For defensive
            // compatibility, accept [action, price, amount] as well.
            if (row.ValueKind != JsonValueKind.Array) continue;
            var values = row.EnumerateArray().ToArray();
            var offset = values.Length == 3 && values[0].ValueKind == JsonValueKind.String ? 1 : 0;
            if (values.Length != offset + 2 ||
                !Decimal(values[offset]).HasValue ||
                !Decimal(values[offset + 1]).HasValue)
                continue;
            var price = Decimal(values[offset])!.Value;
            var amount = Decimal(values[offset + 1])!.Value;
            if (price > 0m && amount >= 0m)
                result.Add(new Level(price, amount));
        }
        return result.ToArray();
    }

    [HttpGet("trades")]
    public async Task<IActionResult> Trades(
        [FromQuery] string instrument,
        [FromQuery] int count = 15,
        CancellationToken ct = default)
    {
        if (!Authorized()) return Denied();
        if (!ValidInstrument(instrument) || count is < 1 or > 30)
            return BadRequest(new { error = "Invalid instrument or trades count." });
        try
        {
            var r = await ReadPublicAsync("public/get_last_trades_by_instrument",
            [
                new("instrument_name", instrument),
                new("count", count.ToString(CultureInfo.InvariantCulture))
            ], ct);
            if (r.ValueKind != JsonValueKind.Object ||
                !r.TryGetProperty("trades", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Unexpected Deribit trades response.");

            return Ok(new {
                instrument,
                trades = rows.EnumerateArray().Take(count).Select(x => new {
                    tradeId = Str(x, "trade_id"),
                    direction = Str(x, "direction"),
                    price = Num(x, "price"),
                    amount = Num(x, "amount"),
                    timestamp = Long(x, "timestamp")
                }).ToArray(),
                receivedAtUtc = DateTimeOffset.UtcNow
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return StatusCode(502, new { error = "Deribit public trades unavailable." });
        }
    }

    [HttpGet("ticker")]
    public async Task<IActionResult> Ticker([FromQuery] string instrument, CancellationToken ct)
    {
        if (!Authorized()) return Denied();
        if (!ValidInstrument(instrument))
            return BadRequest(new { error = "Invalid instrument." });
        try
        {
            var r = await ReadPublicAsync("public/ticker",
                [new("instrument_name", instrument)], ct);
            return Ok(new {
                instrument,
                bestBidPrice = Num(r, "best_bid_price"),
                bestAskPrice = Num(r, "best_ask_price"),
                markPrice = Num(r, "mark_price"),
                lastPrice = Num(r, "last_price"),
                indexPrice = Num(r, "index_price"),
                timestampUtc = DateTimeOffset.UtcNow
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return StatusCode(502, new { error = "Deribit public ticker unavailable." });
        }
    }

    private async Task<JsonElement> ReadPublicAsync(string method,
        IReadOnlyList<KeyValuePair<string, string>> parameters, CancellationToken ct)
    {
        // Closed method set, closed host, fixed path, URL-encoded query fields.
        if (method is not ("public/get_instruments" or "public/get_order_book" or
                "public/get_last_trades_by_instrument" or "public/ticker"))
            throw new InvalidOperationException("Forbidden market-data method.");

        var host = Host;
        var query = string.Join("&", parameters.Select(p =>
            Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        var uri = new Uri("https://" + host + "/api/v2/" + method + "?" + query);
        if (uri.Host != host || uri.Scheme != "https" ||
            uri.AbsolutePath != "/api/v2/" + method || uri.Port != 443)
            throw new InvalidOperationException("Exchange origin mismatch.");

        // Dedicated localhost backend HttpClient disables redirects and proxies.
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        var http = clientFactory.CreateClient("deribit-terminal");
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException("Market-data HTTP failure.");
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream,
            new JsonDocumentOptions { MaxDepth = 32 }, ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("jsonrpc", out var rpc) ||
            rpc.ValueKind != JsonValueKind.String || rpc.GetString() != "2.0" ||
            root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null ||
            !root.TryGetProperty("result", out var result) ||
            result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new InvalidOperationException("Invalid market-data JSON-RPC response.");
        return result.Clone();
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        ? v.GetString() : null;
    private static decimal? Decimal(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var n)) return n;
        if (e.ValueKind == JsonValueKind.String &&
            decimal.TryParse(e.GetString(), NumberStyles.Any,
                CultureInfo.InvariantCulture, out n)) return n;
        return null;
    }
    private static decimal? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ?
        Decimal(v) : null;
    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object &&
        e.TryGetProperty(name, out var v) &&
        v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
