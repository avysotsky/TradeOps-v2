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
        view = View("invalid");

        if (update.ValueKind != JsonValueKind.Object ||
            !update.TryGetProperty("instrument_name", out var instrument) ||
            instrument.GetString() != _instrument ||
            !update.TryGetProperty("type", out var typeField) ||
            !update.TryGetProperty("change_id", out var changeField) ||
            !changeField.TryGetInt64(out var id) ||
            id <= 0 ||
            !update.TryGetProperty("timestamp", out var tsField) ||
            !tsField.TryGetInt64(out var timestamp) || timestamp <= 0 ||
            !update.TryGetProperty("bids", out var bids) ||
            !update.TryGetProperty("asks", out var asks) ||
            bids.ValueKind != JsonValueKind.Array ||
            asks.ValueKind != JsonValueKind.Array)
        {
            Invalidate();
            view = View("invalid");
            return false;
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
                !previous.TryGetInt64(out var previousId) ||
                previousId != _lastChange ||
                id <= _lastChange)
            {
                Invalidate();
                view = View("sequence_gap");
                return false;
            }
        }
        else
        {
            Invalidate();
            view = View("invalid");
            return false;
        }

        if (!ApplySide(bids, _bids, kind == "snapshot") ||
            !ApplySide(asks, _asks, kind == "snapshot") ||
            _bids.Count > 100000 || _asks.Count > 100000)
        {
            Invalidate();
            view = View("invalid");
            return false;
        }

        // Zero-crossing or locked books cannot be used as valid inputs.
        if (_bids.Count > 0 && _asks.Count > 0 &&
            _bids.First().Key >= _asks.First().Key)
        {
            Invalidate();
            view = View("crossed");
            return false;
        }

        _lastChange = id;
        _exchangeTs = timestamp;
        var elapsed = Stopwatch.GetElapsedTime(start);
        view = View("synced", (long)(elapsed.TotalMicroseconds));
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
