using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace TradeOps.Api.Controllers;

/// <summary>
/// Local read-only Deribit terminal. Legacy previews cannot grant order authority;
/// physical execution belongs exclusively to the account-scoped B5 OMS host.
/// </summary>
[ApiController]
[Route("api/deribit-terminal")]
public sealed class DeribitTerminalController(
    IHttpClientFactory clientFactory,
    IConfiguration config) : ControllerBase
{
    private static readonly ConcurrentDictionary<Guid, Preview> Pending = new();
    private static readonly string[] AllowedInstruments =
        ["BTC-PERPETUAL", "ETH-PERPETUAL"];
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromSeconds(45);

    private string EnvironmentName =>
        config["DeribitTerminal:Environment"] ?? "testnet";

    private string Origin => EnvironmentName switch
    {
        "testnet" => "https://test.deribit.com",
        "mainnet" => "https://www.deribit.com",
        _ => throw new InvalidOperationException("Unknown Deribit terminal environment.")
    };

    private bool TradingEnabled =>
        config.GetValue<bool>("DeribitTerminal:ExecutionEnabled") &&
        (EnvironmentName == "testnet" ||
         config.GetValue<bool>("DeribitTerminal:MainnetExecutionEnabled"));

    private decimal MaxContracts
    {
        get
        {
            var value = config.GetValue<decimal?>("DeribitTerminal:MaxContracts") ?? 100m;
            return value is > 0m and <= 100000m ? value : 0m;
        }
    }

    private bool Authorized()
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6
                ? remote.MapToIPv4() : remote))
            return false;

        var expected = config["DeribitTerminal:OperatorKey"];
        if (string.IsNullOrWhiteSpace(expected) || expected.Length < 32)
            return false;

        if (!Request.Headers.TryGetValue("X-TradeOps-Terminal-Key", out var supplied)
            || supplied.Count != 1 || string.IsNullOrWhiteSpace(supplied[0]))
            return false;

        var expectedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var suppliedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(supplied[0]!));
        return CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private IActionResult Denied() => Unauthorized(new { error = "Local terminal authentication required." });

    private bool CredentialsPresent =>
        !string.IsNullOrWhiteSpace(config["DeribitTerminal:ClientId"]) &&
        !string.IsNullOrWhiteSpace(config["DeribitTerminal:ClientSecret"]);

    private static bool ValidInstrument(string? instrument) =>
        instrument is not null && AllowedInstruments.Contains(instrument, StringComparer.Ordinal);

    [HttpGet("status")]
    public IActionResult Status()
    {
        if (!Authorized()) return Denied();
        return Ok(new
        {
            environment = EnvironmentName,
            exchange = "Deribit",
            credentialsConfigured = CredentialsPresent,
            tradingEnabled = false,
            maxContracts = MaxContracts,
            instruments = AllowedInstruments,
            localOnly = true
        });
    }

    [HttpGet("ticker")]
    public async Task<IActionResult> Ticker([FromQuery] string instrument = "BTC-PERPETUAL",
        CancellationToken ct = default)
    {
        if (!Authorized()) return Denied();
        if (!ValidInstrument(instrument)) return BadRequest(new { error = "Unsupported instrument." });

        try
        {
            var result = await GetExchangeAsync("public/ticker",
                [new("instrument_name", instrument)], authenticated: false, ct);
            return Ok(new
            {
                instrument,
                bestBidPrice = Num(result, "best_bid_price"),
                bestAskPrice = Num(result, "best_ask_price"),
                markPrice = Num(result, "mark_price"),
                lastPrice = Num(result, "last_price"),
                indexPrice = Num(result, "index_price"),
                timestampUtc = DateTimeOffset.UtcNow
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return StatusCode(502, new { error = "Deribit ticker unavailable." });
        }
    }

    [HttpGet("account")]
    public async Task<IActionResult> Account([FromQuery] string currency = "BTC",
        CancellationToken ct = default)
    {
        if (!Authorized()) return Denied();
        if (!CredentialsPresent) return Conflict(new { error = "Server-side Deribit credentials not configured." });
        if (currency is not ("BTC" or "ETH")) return BadRequest(new { error = "Unsupported currency." });
        try
        {
            var r = await GetExchangeAsync("private/get_account_summary",
                [new("currency", currency)], true, ct);
            return Ok(new
            {
                currency,
                equity = Num(r, "equity"),
                balance = Num(r, "balance"),
                availableFunds = Num(r, "available_funds"),
                marginBalance = Num(r, "margin_balance")
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return StatusCode(502, new { error = "Deribit account read failed." });
        }
    }

    [HttpGet("positions")]
    public async Task<IActionResult> Positions(CancellationToken ct)
    {
        if (!Authorized()) return Denied();
        if (!CredentialsPresent) return Conflict(new { error = "Server-side Deribit credentials not configured." });
        try
        {
            var r = await GetExchangeAsync("private/get_positions",
                [new("currency", "any")], true, ct);
            if (r.ValueKind != JsonValueKind.Array) throw new InvalidOperationException();
            return Ok(r.EnumerateArray().Select(x => new
            {
                instrument = Str(x, "instrument_name"),
                kind = Str(x, "kind"),
                direction = Str(x, "direction"),
                nativeSize = Num(x, "size"),
                markPrice = Num(x, "mark_price"),
                floatingPnl = Num(x, "floating_profit_loss")
            }).ToArray());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return StatusCode(502, new { error = "Deribit positions read failed." });
        }
    }

    [HttpGet("orders")]
    public async Task<IActionResult> Orders(CancellationToken ct)
    {
        if (!Authorized()) return Denied();
        if (!CredentialsPresent) return Conflict(new { error = "Server-side Deribit credentials not configured." });
        try
        {
            var r = await GetExchangeAsync("private/get_open_orders",
                Array.Empty<KeyValuePair<string, string>>(), true, ct);
            if (r.ValueKind != JsonValueKind.Array) throw new InvalidOperationException();
            return Ok(r.EnumerateArray().Select(x => new
            {
                orderId = Str(x, "order_id"),
                instrument = Str(x, "instrument_name"),
                side = Str(x, "direction"),
                state = Str(x, "order_state"),
                amount = Num(x, "amount"),
                filledAmount = Num(x, "filled_amount"),
                price = Num(x, "price"),
                label = Str(x, "label")
            }).ToArray());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return StatusCode(502, new { error = "Deribit open orders read failed." });
        }
    }

    public sealed record PreviewRequest(string Instrument, string Side, decimal Contracts, decimal Price);
    public sealed record ConfirmRequest(Guid PreviewId);

    private sealed record Preview(Guid Id, string Environment, string Instrument,
        string Side, decimal Contracts, decimal Price, DateTimeOffset ExpiresUtc);

    [HttpPost("orders/preview")]
    public IActionResult PreviewOrder([FromBody] PreviewRequest? request)
    {
        if (!Authorized()) return Denied();
        if (!TradingEnabled || !CredentialsPresent) return Conflict(new { error = "Manual execution disabled on server." });
        if (request is null || !ValidInstrument(request.Instrument) ||
            request.Side is not ("buy" or "sell") ||
            request.Contracts <= 0m || request.Contracts > MaxContracts ||
            request.Price <= 0m || request.Price > 10000000m)
            return BadRequest(new { error = "Invalid order. Only bounded, post-only LIMIT BTC/ETH perpetuals are supported." });

        // Finite bounded in-memory approvals; restart clears previews (safe).
        foreach (var pair in Pending)
            if (pair.Value.ExpiresUtc <= DateTimeOffset.UtcNow)
                Pending.TryRemove(pair.Key, out _);
        if (Pending.Count >= 50) return StatusCode(429, new { error = "Too many pending order previews." });

        var intent = new Preview(Guid.NewGuid(), EnvironmentName, request.Instrument,
            request.Side, request.Contracts, request.Price,
            DateTimeOffset.UtcNow.Add(PreviewLifetime));
        Pending[intent.Id] = intent;
        return Ok(new
        {
            previewId = intent.Id,
            environment = intent.Environment,
            instrument = intent.Instrument,
            side = intent.Side,
            contracts = intent.Contracts,
            price = intent.Price,
            orderType = "limit",
            postOnly = true,
            rejectPostOnly = true,
            expiresUtc = intent.ExpiresUtc,
            warning = intent.Environment == "mainnet" ? "REAL MONEY ORDER" : "TESTNET ORDER"
        });
    }

    [HttpPost("orders/confirm")]
    public async Task<IActionResult> ConfirmOrder([FromBody] ConfirmRequest? request, CancellationToken ct)
    {
        if (!Authorized()) return Denied();
        if (!TradingEnabled || !CredentialsPresent) return Conflict(new { error = "Manual execution disabled on server." });
        if (request is null || !Pending.TryRemove(request.PreviewId, out var intent))
            return Conflict(new { error = "Preview missing or already consumed. Never retry an uncertain order." });
        if (DateTimeOffset.UtcNow >= intent.ExpiresUtc || intent.Environment != EnvironmentName)
            return Conflict(new { error = "Expired or mismatched preview." });

        await Task.CompletedTask;
        return Conflict(new { error = "Direct execution is disabled. Use the account-scoped OMS gateway." });
    }

    public sealed record CancelRequest(string OrderId, string Confirmation);

    [HttpPost("orders/cancel")]
    public async Task<IActionResult> Cancel([FromBody] CancelRequest? request, CancellationToken ct)
    {
        if (!Authorized()) return Denied();
        if (!TradingEnabled || !CredentialsPresent) return Conflict(new { error = "Manual execution disabled on server." });
        if (request is null || string.IsNullOrWhiteSpace(request.OrderId)
            || request.OrderId.Length > 128 || request.OrderId.Any(char.IsControl)
            || request.Confirmation != "CANCEL " + request.OrderId)
            return BadRequest(new { error = "Exact order ID confirmation required." });
        await Task.CompletedTask;
        return Conflict(new { error = "Direct cancellation is disabled. Use the account-scoped OMS gateway." });
    }

    private async Task<JsonElement> GetExchangeAsync(string method,
        IEnumerable<KeyValuePair<string, string>> parameters, bool authenticated, CancellationToken ct)
    {
        if (method is not ("public/get_instrument" or "public/ticker" or "private/get_account_summary" or "private/get_positions" or "private/get_open_orders"))
            throw new InvalidOperationException("Read-only terminal RPC required.");
        if (authenticated && !CredentialsPresent)
            throw new InvalidOperationException("Missing Deribit credentials.");

        var query = string.Join("&", parameters.Select(p =>
            Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        var uri = new Uri(Origin + "/api/v2/" + method + (query.Length > 0 ? "?" + query : ""));
        if (uri.Scheme != "https" ||
            uri.Host != (EnvironmentName == "mainnet" ? "www.deribit.com" : "test.deribit.com") ||
            uri.AbsolutePath != "/api/v2/" + method)
            throw new InvalidOperationException("Exchange target mismatch.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (authenticated)
        {
            var id = config["DeribitTerminal:ClientId"]!;
            var secret = config["DeribitTerminal:ClientSecret"]!;
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(id + ":" + secret)));
        }

        var http = clientFactory.CreateClient("deribit-terminal");
        using var response = await http.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Exchange HTTP error.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            json.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null ||
            !json.RootElement.TryGetProperty("result", out var result) ||
            result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            throw new InvalidOperationException("Exchange response error.");
        return result.Clone();
    }

    private static string? Str(JsonElement source, string name) =>
        source.ValueKind == JsonValueKind.Object &&
        source.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    private static decimal? Num(JsonElement source, string name)
    {
        if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty(name, out var p))
            return null;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var n))
            return n;
        if (p.ValueKind == JsonValueKind.String &&
            decimal.TryParse(p.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out n))
            return n;
        return null;
    }
}
