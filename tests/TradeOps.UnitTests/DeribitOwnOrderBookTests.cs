using System.Text.Json;
using TradeOps.TerminalApi.Market;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitOwnOrderBookTests
{
    private const string Instrument = "ETH-PERPETUAL";

    private static JsonDocument Order(string id, string state, long ts,
        decimal price, decimal amount, decimal filled = 0m, string side = "buy") =>
        JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            instrument_name = Instrument,
            order_id = id,
            order_state = state,
            order_type = "limit",
            direction = side,
            price,
            amount,
            filled_amount = filled,
            last_update_timestamp = ts
        }));

    [Fact]
    public void BootstrapBufferCancellationPreventsStaleSnapshotResurrection()
    {
        var store = new DeribitOwnOrderBook(Instrument);
        // A cancel arrives after subscription, before open-order snapshot.
        using var cancelled = Order("open-1", "cancelled", 101,
            2300m, 10m);
        Assert.True(store.Notification(cancelled.RootElement));
        using var snapshot = JsonDocument.Parse("""
            [{"instrument_name":"ETH-PERPETUAL","order_id":"open-1",
             "order_state":"open","order_type":"limit","direction":"buy",
             "price":2300,"amount":10,"filled_amount":0,
             "last_update_timestamp":100}]
            """);
        Assert.True(store.Hydrate(snapshot.RootElement));
        Assert.Equal("ready", store.View().State);
        Assert.Empty(store.View().Levels);
        Assert.Equal(0, store.View().OpenOrders);
    }

    [Fact]
    public void UpdatesAggregateRemainingAmountByPriceAndSide()
    {
        var book = new DeribitOwnOrderBook(Instrument);
        using var snapshot = JsonDocument.Parse("[]");
        Assert.True(book.Hydrate(snapshot.RootElement));

        using var orderA = Order("abc", "open", 100, 2354.5m, 10, 4);
        using var orderB = Order("def", "open", 101, 2354.5m, 8, 0);
        using var ask = Order("xyz", "open", 102, 2355m, 3, 0, "sell");
        Assert.True(book.Notification(orderA.RootElement));
        Assert.True(book.Notification(orderB.RootElement));
        Assert.True(book.Notification(ask.RootElement));

        var view = book.View();
        Assert.Equal("ready", view.State);
        Assert.Equal(3, view.OpenOrders);
        Assert.Equal(2, view.Levels.Length);
        var bid = Assert.Single(view.Levels.Where(x =>
            x.Price == 2354.5m && x.Direction == "buy"));
        Assert.Equal(14m, bid.Amount);
        Assert.Equal(2, bid.Orders);
        Assert.Equal(3m, Assert.Single(view.Levels.Where(x =>
            x.Price == 2355m && x.Direction == "sell")).Amount);

        // Partial fill adjusts remaining resting size immediately.
        using var filled = Order("abc", "open", 105, 2354.5m, 10, 9);
        Assert.True(book.Notification(filled.RootElement));
        Assert.Equal(9m, Assert.Single(book.View().Levels.Where(x =>
            x.Direction == "buy")).Amount);

        // Stale notification must not resurrect earlier larger amount.
        Assert.True(book.Notification(orderA.RootElement));
        Assert.Equal(9m, Assert.Single(book.View().Levels.Where(x =>
            x.Direction == "buy")).Amount);

        using var closed = Order("abc", "filled", 106, 2354.5m, 10, 10);
        Assert.True(book.Notification(closed.RootElement));
        Assert.Equal(8m, Assert.Single(book.View().Levels.Where(x =>
            x.Direction == "buy")).Amount);
    }

    [Fact]
    public void InvalidOrForeignPayloadFailsClosedAndClearsOwnLevels()
    {
        var book = new DeribitOwnOrderBook(Instrument);
        using var baseline = JsonDocument.Parse("[]");
        Assert.True(book.Hydrate(baseline.RootElement));
        using var open = Order("a", "open", 1, 2350, 5);
        Assert.True(book.Notification(open.RootElement));
        Assert.Single(book.View().Levels);

        using var foreign = JsonDocument.Parse("""
        {"instrument_name":"BTC-PERPETUAL","order_id":"a",
         "order_state":"open","order_type":"limit","direction":"buy",
         "price":2350,"amount":5,"filled_amount":0,
         "last_update_timestamp":2}
        """);
        Assert.False(book.Notification(foreign.RootElement));
        Assert.Equal("unavailable", book.View().State);
        Assert.Empty(book.View().Levels);
    }

    [Fact]
    public void EmptySnapshotIsReadyNoOrders_NotUnavailable()
    {
        var book = new DeribitOwnOrderBook(Instrument);
        using var empty = JsonDocument.Parse("[]");
        Assert.True(book.Hydrate(empty.RootElement));
        Assert.Equal("ready", book.View().State);
        Assert.Equal(0, book.View().OpenOrders);
        Assert.Empty(book.View().Levels);
        book.Reset("resyncing");
        Assert.Equal("resyncing", book.View().State);
        Assert.Empty(book.View().Levels);
    }

    [Fact]
    public void StaticReadOnlyPrivateSubscriptionContractIsNarrow()
    {
        var root = LocateRoot();
        var feed = File.ReadAllText(Path.Combine(root,
            "apps/TradeOps.TerminalApi/Market/DeribitRawFeedHub.cs"));
        var ui = File.ReadAllText(Path.Combine(root,
            "apps/trade-terminal/src/Terminal.tsx"));
        Assert.Contains("user.orders.", feed);
        Assert.Contains("private/subscribe", feed);
        Assert.Contains("private/get_open_orders_by_instrument", feed);
        Assert.Contains("type = \"own_orders\"", feed);
        Assert.Contains("ownOrders = _ownOrders.View()", feed);
        Assert.Contains("BoundedChannelFullMode.DropOldest", feed);
        Assert.Contains("own-order-level", ui);
        Assert.Contains("dom-own-marker", ui);
        Assert.DoesNotContain("private/buy", feed);
        Assert.DoesNotContain("private/sell", feed);
        Assert.DoesNotContain("private/cancel", feed);
    }

    private static string LocateRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TradeOps.sln"))) return d.FullName;
        throw new InvalidOperationException("TradeOps root not found.");
    }
}
