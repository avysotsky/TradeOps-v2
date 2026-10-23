using System.Globalization;
using System.Text.Json;

namespace TradeOps.Infrastructure.Exchange.Deribit;

/// <summary>
/// Pure, offline normalization of the authenticated Deribit READ-ONLY
/// position and trade-history result payloads. No transport, DI or
/// account mutation capability. Raw amounts are NEVER called contracts.
/// </summary>
public static class DeribitReadOnlyEvidenceParser
{
    public static IReadOnlyList<DeribitPositionEvidence> ParsePositions(
        JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array)
            throw Invalid();

        var parsed = new List<DeribitPositionEvidence>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in result.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                throw Invalid();

            var name = RequiredString(row, "instrument_name");
            var kind = RequiredString(row, "kind");
            var direction = RequiredString(row, "direction");

            if (kind is not ("future" or "option"
                or "future_combo" or "option_combo")
                || direction is not ("buy" or "sell" or "zero")
                || !seen.Add(name))
                throw Invalid();

            var size = RequiredDecimal(row, "size");
            if (size < 0m || (size > 0m && direction == "zero"))
                throw Invalid();

            var sizeCurrency = OptionalDecimal(row, "size_currency");
            if (sizeCurrency is < 0m)
                throw Invalid();

            // The response documents size_currency only for futures.
            // For an option this is not a permissible quantity substitute.
            if (kind is "option" && sizeCurrency is not null)
                throw Invalid();

            // Record the exchange native units. Do NOT infer underlying,
            // expiry, option delta, contract multiplier or net USD notionals.
            parsed.Add(new DeribitPositionEvidence(
                name, kind, direction, size, sizeCurrency));
        }

        return parsed.AsReadOnly();
    }

    /// <summary>
    /// Parses a SINGLE verified response page. A false has_more flag proves
    /// only API pagination exhausted for this query, not trade-history
    /// completeness across the live/historical indexing gap or subaccounts.
    /// Caller must independently bind account, currency and time window.
    /// </summary>
    public static DeribitTradeEvidencePage ParseTradePage(
        JsonElement result, string currency, long startMs, long endMs,
        bool historical)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("trades", out var trades)
            || trades.ValueKind != JsonValueKind.Array
            || !result.TryGetProperty("has_more", out var more)
            || more.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || string.IsNullOrWhiteSpace(currency)
            || currency.Length > 16
            || !currency.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            || startMs <= 0 || endMs < startMs)
            throw Invalid();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var observations = new List<DeribitTradeEvidence>();

        foreach (var row in trades.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                throw Invalid();

            var tradeId = RequiredString(row, "trade_id");
            var orderId = RequiredString(row, "order_id");
            var instrument = RequiredString(row, "instrument_name");
            var direction = RequiredString(row, "direction");

            if (!seen.Add(tradeId)
                || direction is not ("buy" or "sell"))
                throw Invalid();

            var timestamp = RequiredInt64(row, "timestamp");
            if (timestamp < startMs || timestamp > endMs)
                throw Invalid();

            var amount = RequiredDecimal(row, "amount");
            var price = RequiredDecimal(row, "price");
            var fee = RequiredDecimal(row, "fee");
            var feeCurrency = RequiredString(row, "fee_currency");

            if (amount <= 0m || price <= 0m || feeCurrency.Length > 16)
                throw Invalid();

            // direction in user_trade is NOT a trusted normalized
            // account position-side, and amount is not contracts.
            observations.Add(new DeribitTradeEvidence(
                tradeId, orderId, instrument, direction,
                amount, price, fee, feeCurrency, timestamp,
                OptionalDecimal(row, "contracts")));
        }

        if (more.GetBoolean() && observations.Count == 0)
            throw Invalid();

        return new DeribitTradeEvidencePage(
            currency, startMs, endMs, historical,
            more.GetBoolean(), observations.AsReadOnly());
    }

    private static string RequiredString(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out var item)
            || item.ValueKind != JsonValueKind.String)
            throw Invalid();

        var value = item.GetString()!;
        if (value.Length == 0 || value.Length > 160
            || value != value.Trim() || value.Any(char.IsControl))
            throw Invalid();

        return value;
    }

    private static decimal RequiredDecimal(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out var item)
            || !TryDecimal(item, out var value))
            throw Invalid();

        return value;
    }

    private static decimal? OptionalDecimal(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out var item)
            || item.ValueKind == JsonValueKind.Null)
            return null;

        if (!TryDecimal(item, out var value))
            throw Invalid();

        return value;
    }

    private static bool TryDecimal(JsonElement item, out decimal value)
    {
        value = default;
        if (item.ValueKind != JsonValueKind.Number)
            return false;
        return decimal.TryParse(item.GetRawText(),
            NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static long RequiredInt64(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out var item)
            || item.ValueKind != JsonValueKind.Number
            || !item.TryGetInt64(out var value))
            throw Invalid();

        return value;
    }

    private static InvalidOperationException Invalid() =>
        new("Untrusted Deribit evidence: missing, inconsistent or out-of-range data.");
}

public sealed record DeribitPositionEvidence(
    string InstrumentName,
    string Kind,
    string Direction,
    decimal NativeSize,
    decimal? BaseCurrencySize);

public sealed record DeribitTradeEvidence(
    string TradeId,
    string OrderId,
    string InstrumentName,
    string RawDirection,
    decimal NativeAmount,
    decimal Price,
    decimal Fee,
    string FeeCurrency,
    long TimestampMs,
    decimal? Contracts);

public sealed record DeribitTradeEvidencePage(
    string Currency,
    long StartMs,
    long EndMs,
    bool Historical,
    bool HasMore,
    IReadOnlyList<DeribitTradeEvidence> Trades)
{
    // Indexing/coverage and account completeness require a separate
    // authenticated multi-page collector and cannot be claimed here.
    public bool IsHistoricalCoverageProven => false;
}
