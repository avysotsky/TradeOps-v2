namespace TradeOps.Infrastructure.Exchange.Deribit;

/// <summary>
/// Reads a bounded, currency-scoped Deribit trade-history window.
/// Splits timestamp windows on has_more; avoids cursor assumptions.
/// Every returned page is parsed with the existing strict evidence parser.
/// Exhaustion of API pages is NOT proof of live/historical index completeness.
/// </summary>
public static class DeribitTradeHistoryCollector
{
    public const int PageSize = 1000;
    public const int MaximumCallsPerMode = 32;
    public const int MaximumTradesPerMode = 10000;
    public const long MaximumWindowMs = 30L * 24 * 60 * 60 * 1000;

    public static void ValidateScope(
        string currency, long startMs, long endMs)
    {
        if (string.IsNullOrWhiteSpace(currency)
            || currency.Length > 16
            || !currency.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            || startMs <= 0 || endMs < startMs
            || endMs - startMs > MaximumWindowMs)
        {
            throw new ArgumentException(
                "Trade history query requires an uppercase currency and bounded UTC millisecond range.");
        }
    }

    public static async Task<DeribitTradeWindowEvidence> CollectAsync(
        string currency,
        long startMs,
        long endMs,
        bool historical,
        Func<string, long, long, bool,
            CancellationToken, Task<DeribitTradeEvidencePage>> loadPage,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(currency, startMs, endMs);
        ArgumentNullException.ThrowIfNull(loadPage);

        var records = new Dictionary<string, DeribitTradeEvidence>(
            StringComparer.Ordinal);
        var pending = new Stack<(long Start, long End)>();
        pending.Push((startMs, endMs));
        var calls = 0;
        var duplicates = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (calls >= MaximumCallsPerMode)
                throw new InvalidOperationException(
                    "Deribit trade-history paging budget exceeded; snapshot is not complete.");

            var (from, to) = pending.Pop();
            var page = await loadPage(currency, from, to, historical,
                cancellationToken).ConfigureAwait(false);
            calls++;

            if (page.Currency != currency || page.StartMs != from
                || page.EndMs != to || page.Historical != historical
                || page.Trades.Count > PageSize)
            {
                throw new InvalidOperationException(
                    "Deribit trade-history page scope is inconsistent.");
            }

            foreach (var item in page.Trades)
            {
                // Never trust a callback to preserve the parser's timestamp
                // boundary; inspect again before accepting any evidence.
                if (item.TimestampMs < from || item.TimestampMs > to)
                    throw new InvalidOperationException(
                        "Deribit trade-history trade outside requested window.");

                if (records.TryGetValue(item.TradeId, out var existing))
                {
                    if (existing != item)
                        throw new InvalidOperationException(
                            "Deribit trade ID was reused with conflicting evidence.");
                    duplicates++;
                }
                else
                {
                    records.Add(item.TradeId, item);
                }

                if (records.Count > MaximumTradesPerMode)
                    throw new InvalidOperationException(
                        "Deribit trade-history evidence budget exceeded.");
            }

            if (!page.HasMore)
                continue;

            // No ID cursor exists on the time-scoped endpoint. Split
            // the INCLUSIVE range, dedup boundary timestamp by trade_id.
            // If more than one page shares one millisecond, do not guess:
            // explicit failure, never a silently incomplete "success".
            if (from == to)
                throw new InvalidOperationException(
                    "Deribit trade-history is truncated within a single millisecond.");

            var middle = from + (to - from) / 2;
            var nextStart = middle == from ? from + 1 : middle;
            pending.Push((nextStart, to));
            pending.Push((from, middle));
        }

        return new DeribitTradeWindowEvidence(
            currency, startMs, endMs, historical, calls,
            records.Count, duplicates,
            ApiPagesExhausted: true,
            HistoricalCoverageProven: false);
    }
}

public sealed record DeribitTradeWindowEvidence(
    string Currency,
    long StartMs,
    long EndMs,
    bool Historical,
    int PageCalls,
    int UniqueTrades,
    int DuplicateBoundaryTrades,
    bool ApiPagesExhausted,
    bool HistoricalCoverageProven);
