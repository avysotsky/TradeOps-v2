using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;

namespace TradeOps.Connectors.Deribit;

public sealed record DeribitInstrumentMetadata(string Name, string Kind, string InstrumentType,
    string BaseCurrency, string QuoteCurrency, string SettlementCurrency, bool IsActive,
    OmsPrivateAmountUnit AmountUnit, string AmountCurrency);

/// <summary>One bounded public query, not account coverage, historical metadata or a readiness gate.</summary>
public sealed class DeribitInstrumentCatalogSnapshot
{
    public OmsEnvironment Environment { get; }
    public string QueryCurrency { get; }
    public string Kind { get; }
    public DateTimeOffset ReceivedAtUtc { get; }
    public IReadOnlyList<DeribitInstrumentMetadata> Instruments { get; }

    internal DeribitInstrumentCatalogSnapshot(OmsEnvironment environment, string currency, string kind,
        DateTimeOffset received, DeribitInstrumentMetadata[] instruments)
    { Environment = environment; QueryCurrency = currency; Kind = kind; ReceivedAtUtc = received;
        Instruments = Array.AsReadOnly(instruments); }
}

/// <summary>Public metadata only. Callers supply a redirect/cookie-free HttpClient without credentials.</summary>
public sealed class DeribitInstrumentCatalog(HttpClient http)
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private const int MaximumInstruments = 4096;

    public async Task<DeribitInstrumentCatalogSnapshot> ReadAsync(OmsEnvironment environment, string currency,
        string kind, CancellationToken ct = default)
    {
        if (environment is not (OmsEnvironment.Testnet or OmsEnvironment.Mainnet) || !Token(currency) || currency == "ANY" ||
            kind is not ("future" or "option") || http.DefaultRequestHeaders.Authorization is not null ||
            http.DefaultRequestHeaders.Contains("Cookie"))
            throw new ArgumentException("Invalid credential-free public instrument query.");
        var host = environment == OmsEnvironment.Testnet ? "test.deribit.com" : "www.deribit.com";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://{host}/api/v2/public/get_instruments?currency={currency}&kind={kind}&expired=false");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumBytes)
                throw new InvalidOperationException();
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int count;
            while ((count = await input.ReadAsync(chunk, deadline.Token)) != 0)
            {
                if (buffer.Length + count > MaximumBytes) throw new InvalidOperationException();
                buffer.Write(chunk, 0, count);
            }
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length),
                new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (String(root, "jsonrpc") != "2.0" || root.TryGetProperty("error", out _) ||
                !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array ||
                result.GetArrayLength() > MaximumInstruments) throw new InvalidOperationException();
            var items = new List<DeribitInstrumentMetadata>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in result.EnumerateArray())
            {
                var name = String(item, "instrument_name");
                var type = String(item, "instrument_type");
                var baseCurrency = String(item, "base_currency");
                var quoteCurrency = String(item, "quote_currency");
                var settlementCurrency = String(item, "settlement_currency");
                if (name.Length is < 1 or > 160 || name != name.Trim() || name.Any(char.IsControl) ||
                    !names.Add(name) || String(item, "kind") != kind || type is not ("linear" or "reversed") ||
                    !Token(baseCurrency) || !Token(quoteCurrency) || !Token(settlementCurrency) ||
                    !item.TryGetProperty("is_active", out var active) || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidOperationException();
                var inverseFuture = kind == "future" && type == "reversed";
                if (inverseFuture && (quoteCurrency != "USD" || settlementCurrency != baseCurrency))
                    throw new InvalidOperationException();
                items.Add(new(name, kind, type, baseCurrency, quoteCurrency, settlementCurrency, active.GetBoolean(),
                    inverseFuture ? OmsPrivateAmountUnit.Usd : OmsPrivateAmountUnit.BaseCoin,
                    inverseFuture ? "USD" : baseCurrency));
            }
            return new(environment, currency, kind, DateTimeOffset.UtcNow, items.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new InvalidOperationException("Deribit public instrument catalog read failed."); }
    }

    /// <summary>Checks explicitly configured units/names; never assigns account or subscription currency.</summary>
    public static void ValidatePlan(OmsEnvironment environment, DeribitPrivateCapturePlan plan,
        IReadOnlyCollection<DeribitInstrumentCatalogSnapshot> snapshots)
    {
        if (snapshots.Count > 32 || snapshots.Any(s => s.Environment != environment))
            throw new InvalidOperationException("Deribit public instrument manifest validation failed.");
        foreach (var expected in plan.Instruments.Values)
        {
            var groups = snapshots.Where(s => s.QueryCurrency == expected.Currency && s.Kind == expected.Kind).ToArray();
            var actual = groups.Length == 1 ? groups[0].Instruments.SingleOrDefault(x => x.Name == expected.Name) : null;
            if (actual is null || actual.AmountUnit != expected.AmountUnit || actual.AmountCurrency != expected.AmountCurrency)
                throw new InvalidOperationException("Deribit public instrument manifest validation failed.");
        }
    }

    private static string String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new InvalidOperationException();
    private static bool Token(string? value) => value is { Length: > 0 and <= 32 } &&
        value.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_');
}
