using System.Globalization;
using System.Text.Json;
using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Ibkr;

public sealed class IbkrInstrumentResolver
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IbkrOptions _options;

    public IbkrInstrumentResolver(
        IHttpClientFactory httpClientFactory,
        IbkrOptions options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public async Task<IbkrResolvedInstrument> ResolveAsync(
        InstrumentReference instrument,
        CancellationToken cancellationToken = default)
    {
        ValidateInstrument(instrument);

        if (!string.IsNullOrWhiteSpace(instrument.VenueInstrumentId))
        {
            if (!long.TryParse(
                    instrument.VenueInstrumentId,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var conid)
                || conid <= 0)
            {
                throw new ArgumentException(
                    "IBKR VenueInstrumentId must be a positive numeric conid.",
                    nameof(instrument));
            }

            var known = await GetContractInfoAsync(
                conid,
                cancellationToken);

            if (!Matches(instrument, known))
            {
                throw new InvalidOperationException(
                    $"IBKR conid '{conid}' does not match the requested stock identity.");
            }

            return ToResolved(known, instrument);
        }

        var symbol = instrument.Symbol.Trim().ToUpperInvariant();
        var search = await GetJsonAsync(
            $"/iserver/secdef/search?symbol={Uri.EscapeDataString(symbol)}&secType=STK",
            cancellationToken);

        if (search.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "IBKR contract search returned an invalid response.");
        }

        var conids = search
            .EnumerateArray()
            .Where(item =>
                string.Equals(
                    ReadString(item, "symbol"),
                    symbol,
                    StringComparison.OrdinalIgnoreCase))
            .Select(item => ReadLong(item, "conid"))
            .Where(value => value > 0)
            .Distinct()
            .ToArray();

        var matches = new List<IbkrResolvedInstrument>();

        foreach (var conid in conids)
        {
            var info = await GetContractInfoAsync(
                conid,
                cancellationToken);

            if (Matches(instrument, info))
            {
                matches.Add(ToResolved(info, instrument));
            }
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new KeyNotFoundException(
                $"IBKR stock contract for '{instrument.Symbol}' was not found for the requested currency/exchange."),
            _ => throw new InvalidOperationException(
                $"IBKR stock contract for '{instrument.Symbol}' is ambiguous; specify VenueInstrumentId/conid.")
        };
    }

    private async Task<JsonElement> GetContractInfoAsync(
        long conid,
        CancellationToken cancellationToken) =>
        await GetJsonAsync(
            $"/iserver/contract/{conid.ToString(CultureInfo.InvariantCulture)}/info",
            cancellationToken);

    private async Task<JsonElement> GetJsonAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(
            IbkrPaperExchangeClient.HttpClientName);

        using var response = await client.GetAsync(
            BuildUrl(path),
            cancellationToken);

        var json = await response.Content.ReadAsStringAsync(
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"IBKR Web API returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private bool Matches(
        InstrumentReference requested,
        JsonElement info)
    {
        var secType = ReadFirstString(
            info,
            "secType",
            "instrument_type");

        if (!string.Equals(
                secType,
                "STK",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var symbol = ReadFirstString(
            info,
            "ticker",
            "symbol",
            "local_symbol");

        if (!string.Equals(
                symbol,
                requested.Symbol.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var currency = ReadString(info, "currency");

        if (!string.IsNullOrWhiteSpace(requested.Currency)
            && !string.Equals(
                currency,
                requested.Currency.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(requested.Exchange))
        {
            return true;
        }

        var requestedExchange =
            requested.Exchange.Trim().ToUpperInvariant();
        var listingExchange = ReadFirstString(
            info,
            "listingExchange",
            "listing_exchange",
            "exchange");
        var validExchanges = ReadFirstString(
            info,
            "validExchanges",
            "valid_exchanges");

        if (ExchangeMatches(
                listingExchange,
                requestedExchange))
        {
            return true;
        }

        return validExchanges
            .Split(
                ',',
                StringSplitOptions.TrimEntries
                | StringSplitOptions.RemoveEmptyEntries)
            .Any(exchange =>
                ExchangeMatches(
                    exchange,
                    requestedExchange));
    }

    private static bool ExchangeMatches(
        string candidate,
        string requested) =>
        string.Equals(
            candidate,
            requested,
            StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(
            requested + ".",
            StringComparison.OrdinalIgnoreCase);

    private static IbkrResolvedInstrument ToResolved(
        JsonElement info,
        InstrumentReference requested)
    {
        var conid = ReadFirstLong(
            info,
            "conid",
            "con_id");

        var exchange = string.IsNullOrWhiteSpace(requested.Exchange)
            ? ReadFirstString(
                info,
                "listingExchange",
                "listing_exchange",
                "exchange")
            : requested.Exchange.Trim().ToUpperInvariant();

        return new IbkrResolvedInstrument(
            conid,
            ReadFirstString(
                info,
                "ticker",
                "symbol",
                "local_symbol")
                .ToUpperInvariant(),
            ReadString(info, "currency").ToUpperInvariant(),
            exchange);
    }

    private static void ValidateInstrument(
        InstrumentReference instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);

        if (instrument.AssetClass != AssetClass.Stock)
        {
            throw new NotSupportedException(
                "IBKR first milestone resolves Stock instruments only.");
        }

        if (string.IsNullOrWhiteSpace(instrument.Symbol))
        {
            throw new ArgumentException(
                "IBKR stock symbol is required.",
                nameof(instrument));
        }
    }

    private string BuildUrl(string path) =>
        $"{_options.BaseUrl.TrimEnd('/')}{path}";

    private static string ReadFirstString(
        JsonElement element,
        params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadString(element, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string ReadString(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static long ReadFirstLong(
        JsonElement element,
        params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadLong(element, name);
            if (value > 0)
            {
                return value;
            }
        }

        return 0;
    }

    private static long ReadLong(
        JsonElement element,
        string name)
    {
        var raw = ReadString(element, name);

        return long.TryParse(
            raw,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0;
    }
}
