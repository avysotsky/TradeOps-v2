using System.Text.Json;

namespace TradeOps.TerminalApi.Market;

/// <summary>
/// Single-threaded, private, per-instrument read-only order state.
/// Initial snapshot is reconciled with WS notifications buffered while the
/// snapshot was in flight. Terminal notifications tombstone orders so a stale
/// snapshot cannot resurrect them. The UI sees only aggregated volumes.
/// </summary>
public sealed class DeribitOwnOrderBook(string instrument)
{
    public sealed record OwnLevel(string Direction, decimal Price, decimal Amount, int Orders);
    public sealed record OwnView(string Instrument, string State,
        OwnLevel[] Levels, int OpenOrders, long SnapshotUtcMs);

    private sealed record OrderEntry(string OrderId, string Direction, decimal Price,
        decimal Remaining, long UpdatedAt);
    private sealed record Revision(long UpdatedAt, OrderEntry? Order);

    private readonly Dictionary<string, Revision> _orders = new(StringComparer.Ordinal);
    private readonly List<JsonElement> _pending = [];
    private const int MaxOrders = 3000;
    private const int MaxBuffered = 2000;
    private bool _ready;
    private long _snapshotUtcMs;

    public string State { get; private set; } = "loading";

    public void Reset(string state = "loading")
    {
        _ready = false;
        _orders.Clear();
        _pending.Clear();
        _snapshotUtcMs = 0;
        State = state;
    }

    public bool Notification(JsonElement data)
    {
        if (!TryRead(data, out var id, out var revision)) return Invalidate();
        if (!_ready)
        {
            if (_pending.Count >= MaxBuffered) return Invalidate();
            _pending.Add(data.Clone());
            return true;
        }
        return Apply(id, revision);
    }

    public bool Hydrate(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array ||
            result.GetArrayLength() > MaxOrders)
            return Invalidate();

        // Existing WS notifications MUST be replayed after snapshot to
        // prevent the HTTP/WS bootstrap race (new order vs stale snapshot).
        _orders.Clear();
        foreach (var item in result.EnumerateArray())
        {
            if (!TryRead(item, out var id, out var revision) ||
                !Apply(id, revision)) return Invalidate();
        }
        foreach (var change in _pending)
        {
            if (!TryRead(change, out var id, out var revision) ||
                !Apply(id, revision)) return Invalidate();
        }
        _pending.Clear();
        _ready = true;
        _snapshotUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        State = "ready";
        return true;
    }

    private bool Invalidate()
    {
        Reset("unavailable");
        return false;
    }

    private bool Apply(string id, Revision revision)
    {
        if (_orders.TryGetValue(id, out var previous) &&
            previous.UpdatedAt > revision.UpdatedAt) return true;
        if (_orders.Count >= MaxOrders && !_orders.ContainsKey(id))
            return Invalidate();
        _orders[id] = revision;
        return true;
    }

    private bool TryRead(JsonElement item, out string id, out Revision revision)
    {
        id = "";
        revision = default!;
        if (item.ValueKind != JsonValueKind.Object ||
            String(item, "instrument_name") != instrument)
            return false;
        var orderId = String(item, "order_id");
        var state = String(item, "order_state");
        var direction = String(item, "direction");
        var orderType = String(item, "order_type");
        var timestamp = Long(item, "last_update_timestamp");
        if (string.IsNullOrEmpty(orderId) || orderId.Length > 100 ||
            state is null || direction is not ("buy" or "sell") ||
            timestamp is null || timestamp <= 0)
            return false;
        id = orderId;
        // Only actively resting limit prices qualify as order-book overlays.
        // Completed/cancelled/triggered/market orders are tombstones.
        OrderEntry? entry = null;
        if (state == "open" && orderType == "limit" &&
            Decimal(item, "price") is decimal price && price > 0 &&
            Decimal(item, "amount") is decimal amount &&
            Decimal(item, "filled_amount") is decimal filled &&
            filled >= 0 && amount > filled)
        {
            entry = new OrderEntry(id, direction, price, amount - filled, timestamp.Value);
        }
        revision = new Revision(timestamp.Value, entry);
        return true;
    }

    private static string? String(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Long(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;

    private static decimal? Decimal(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetDecimal(out var n) ? n : null;

    public OwnView View()
    {
        if (!_ready) return new OwnView(instrument, State, [], 0, 0);
        var active = _orders.Values.Where(x => x.Order is not null)
            .Select(x => x.Order!).ToArray();
        return new OwnView(instrument, State,
            active.GroupBy(x => (x.Direction, x.Price))
                .Select(group => new OwnLevel(group.Key.Direction, group.Key.Price,
                    group.Sum(order => order.Remaining), group.Count()))
                .OrderBy(x => x.Price).ThenBy(x => x.Direction)
                .ToArray(),
            active.Length, _snapshotUtcMs);
    }
}
