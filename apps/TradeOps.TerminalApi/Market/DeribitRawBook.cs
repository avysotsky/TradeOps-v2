using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace TradeOps.TerminalApi.Market;

/// <summary>
/// Single-writer, per-instrument, full-depth L2 reconstruction from Deribit
/// book.{instrument}.raw snapshot/change notifications. Never expose a
/// partially applied or sequence-gapped book as tradable data.
/// </summary>
public sealed class DeribitRawBook
{
    public sealed record Level(decimal Price, decimal Amount);
    public sealed record BookView(
        string Instrument, string State, long? ChangeId, long? ExchangeTimestamp,
        long LocalReceivedAtUnixMs, long ParseToSnapshotUs,
        Level[] Bids, Level[] Asks);

    private readonly string _instrument;
    private readonly SortedDictionary<decimal, decimal> _bids =
        new(Comparer<decimal>.Create((a, b) => b.CompareTo(a)));
    private readonly SortedDictionary<decimal, decimal> _asks = new();
    private long? _lastChange;
    private long? _exchangeTs;

    // The book itself fails closed. These IDs are exchange sequence numbers,
    // not account order IDs; safe to expose as bounded diagnostics.
    public sealed record BookFailure(
        string Code, long? ExpectedChangeId, long? PrevChangeId, long? ChangeId);
    public BookFailure? LastFailure { get; private set; }

    private bool Fail(out BookView view, string state, string code,
        long? prevId = null, long? changeId = null)
    {
        var expected = _lastChange;
        Invalidate();
        LastFailure = new BookFailure(code, expected, prevId, changeId);
        view = View(state);
        return false;
    }

    private static long? Id(JsonElement data, string property) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var id) ? id : null;

    public DeribitRawBook(string instrument) => _instrument = instrument;

    public const int MaxVisibleLevels = 50;

    public BookView View(string state, long parseUs = 0)
    {
        return new BookView(_instrument, state, _lastChange, _exchangeTs,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), parseUs,
            _bids.Take(MaxVisibleLevels).Select(x => new Level(x.Key, x.Value)).ToArray(),
            _asks.Take(MaxVisibleLevels).Select(x => new Level(x.Key, x.Value)).ToArray());
    }

    public void Invalidate()
    {
        _lastChange = null;
        _exchangeTs = null;
        _bids.Clear();
        _asks.Clear();
    }

    public bool Apply(JsonElement update, out BookView view)
    {
        var start = Stopwatch.GetTimestamp();
        LastFailure = null;
        // Avoid copying up to 50 levels twice per valid RAW delta.
        // Error paths below explicitly invalidate and clear the book.
        view = new BookView(_instrument, "invalid", null, null,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0, [], []);

        if (update.ValueKind != JsonValueKind.Object ||
            !update.TryGetProperty("instrument_name", out var instrument) ||
            instrument.ValueKind != JsonValueKind.String ||
            instrument.GetString() != _instrument ||
            !update.TryGetProperty("type", out var typeField) ||
            typeField.ValueKind != JsonValueKind.String ||
            !update.TryGetProperty("change_id", out var changeField) ||
            changeField.ValueKind != JsonValueKind.Number ||
            !changeField.TryGetInt64(out var id) ||
            id <= 0 ||
            !update.TryGetProperty("timestamp", out var tsField) ||
            tsField.ValueKind != JsonValueKind.Number ||
            !tsField.TryGetInt64(out var timestamp) || timestamp <= 0 ||
            !update.TryGetProperty("bids", out var bids) ||
            !update.TryGetProperty("asks", out var asks) ||
            bids.ValueKind != JsonValueKind.Array ||
            asks.ValueKind != JsonValueKind.Array)
        {
            return Fail(out view, "invalid", "L2_MALFORMED",
                Id(update, "prev_change_id"), Id(update, "change_id"));
        }

        var kind = typeField.GetString();
        if (kind == "snapshot")
        {
            Invalidate();
        }
        else if (kind == "change")
        {
            if (_lastChange is null ||
                !update.TryGetProperty("prev_change_id", out var previous) ||
                previous.ValueKind != JsonValueKind.Number ||
                !previous.TryGetInt64(out var previousId) ||
                previousId != _lastChange ||
                id <= _lastChange)
            {
                return Fail(out view, "sequence_gap", "L2_PREV_CHANGE_MISMATCH",
                    Id(update, "prev_change_id"), id);
            }
        }
        else
        {
            return Fail(out view, "invalid", "L2_MALFORMED",
                Id(update, "prev_change_id"), id);
        }

        if (!ApplySide(bids, _bids, kind == "snapshot") ||
            !ApplySide(asks, _asks, kind == "snapshot") ||
            _bids.Count > 100000 || _asks.Count > 100000)
        {
            return Fail(out view, "invalid", "L2_MALFORMED",
                Id(update, "prev_change_id"), id);
        }

        // Zero-crossing or locked books cannot be used as valid inputs.
        if (_bids.Count > 0 && _asks.Count > 0 &&
            _bids.First().Key >= _asks.First().Key)
        {
            return Fail(out view, "crossed", "L2_CROSSED",
                Id(update, "prev_change_id"), id);
        }

        _lastChange = id;
        _exchangeTs = timestamp;
        var elapsed = Stopwatch.GetElapsedTime(start);
        view = View("synced", (long)(elapsed.TotalMicroseconds));
        return true;
    }

    /// <summary>
    /// Depth-limited grouped Deribit channel: every notification is a complete
    /// bounded book image, not a RAW incremental change. Accept both the
    /// exchange's [price, amount] levels and [action, price, amount] snapshot
    /// representation. Never graft a partial grouped image onto prior depth.
    /// </summary>
    public bool ApplyGroupedSnapshot(JsonElement update, int depth, out BookView view)
    {
        LastFailure = null;
        view = View("invalid");
        if (depth != 10 ||
            update.ValueKind != JsonValueKind.Object ||
            !update.TryGetProperty("instrument_name", out var instrument) ||
            instrument.ValueKind != JsonValueKind.String ||
            instrument.GetString() != _instrument ||
            !update.TryGetProperty("timestamp", out var time) ||
            time.ValueKind != JsonValueKind.Number ||
            !time.TryGetInt64(out var timestamp) || timestamp <= 0 ||
            !update.TryGetProperty("bids", out var bids) ||
            !update.TryGetProperty("asks", out var asks) ||
            bids.ValueKind != JsonValueKind.Array ||
            asks.ValueKind != JsonValueKind.Array ||
            bids.GetArrayLength() > depth ||
            asks.GetArrayLength() > depth ||
            !TryReadGroupedSide(bids, out var nextBids) ||
            !TryReadGroupedSide(asks, out var nextAsks))
        {
            return Fail(out view, "invalid", "L2_GROUPED_MALFORMED",
                changeId: Id(update, "change_id"));
        }

        if (nextBids.Count > 0 && nextAsks.Count > 0 &&
            nextBids.Max(x => x.Key) >= nextAsks.Min(x => x.Key))
            return Fail(out view, "crossed", "L2_CROSSED",
                changeId: Id(update, "change_id"));

        Invalidate();
        foreach (var level in nextBids) _bids.Add(level.Key, level.Value);
        foreach (var level in nextAsks) _asks.Add(level.Key, level.Value);
        _exchangeTs = timestamp;
        _lastChange = Id(update, "change_id");
        view = View("synced");
        return true;
    }

    private static bool TryReadGroupedSide(JsonElement rows,
        out Dictionary<decimal, decimal> levels)
    {
        levels = new Dictionary<decimal, decimal>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array ||
                row.GetArrayLength() is not (2 or 3))
                return false;
            var parts = row.EnumerateArray().ToArray();
            var offset = parts.Length == 3 ? 1 : 0;
            if (offset == 1 &&
                (parts[0].ValueKind != JsonValueKind.String ||
                 parts[0].GetString() != "new"))
                return false;
            if (!Read(parts[offset], out var price) ||
                !Read(parts[offset + 1], out var amount) ||
                price <= 0m || amount <= 0m || !levels.TryAdd(price, amount))
                return false;
        }
        return true;
    }

    private static bool ApplySide(JsonElement levels,
        SortedDictionary<decimal, decimal> side, bool snapshot)
    {
        foreach (var row in levels.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 3)
                return false;
            var parts = row.EnumerateArray().ToArray();
            if (parts[0].ValueKind != JsonValueKind.String ||
                !Read(parts[1], out var price) || !Read(parts[2], out var amount) ||
                price <= 0m || amount < 0m)
                return false;
            var action = parts[0].GetString();
            if (action == "delete" || amount == 0m)
            {
                if (snapshot && action == "delete") return false;
                side.Remove(price);
            }
            else if (action is "new" or "change")
            {
                side[price] = amount;
            }
            else return false;
        }
        return true;
    }

    private static bool Read(JsonElement element, out decimal result)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetDecimal(out result);
        if (element.ValueKind == JsonValueKind.String)
            return decimal.TryParse(element.GetString(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out result);
        result = default;
        return false;
    }
}
