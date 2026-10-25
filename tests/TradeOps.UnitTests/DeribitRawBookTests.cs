using System.Text.Json;
using TradeOps.TerminalApi.Market;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitRawBookTests
{
    private const string Instrument = "ETH-PERPETUAL";

    private static JsonDocument Frame(string data) => JsonDocument.Parse(data);

    [Fact]
    public void SnapshotAndChainedChanges_ReconstructFullL2Correctly()
    {
        var book = new DeribitRawBook(Instrument);
        using var snap = Frame("""
        {"instrument_name":"ETH-PERPETUAL","type":"snapshot","change_id":101,
         "timestamp":1790000000000,"bids":[["new",2000,10],["new",1999.5,5]],
         "asks":[["new",2001,7],["new",2002,4]]}
        """);
        Assert.True(book.Apply(snap.RootElement, out var first));
        Assert.Equal("synced", first.State);
        Assert.Equal(101, first.ChangeId);
        Assert.Equal(2000m, first.Bids[0].Price);
        Assert.Equal(2001m, first.Asks[0].Price);

        using var change = Frame("""
        {"instrument_name":"ETH-PERPETUAL","type":"change","change_id":102,
         "prev_change_id":101,"timestamp":1790000000002,
         "bids":[["change",2000,11],["delete",1999.5,0],["new",1999,3]],
         "asks":[["delete",2001,0],["new",2000.5,9]]}
        """);
        Assert.True(book.Apply(change.RootElement, out var second));
        Assert.Equal(11m, second.Bids[0].Amount);
        Assert.Equal(1999m, second.Bids[1].Price);
        Assert.Single(second.Bids.Where(x => x.Price == 1999m));
        Assert.Equal(2000.5m, second.Asks[0].Price);
        Assert.Equal(102, second.ChangeId);
        Assert.Equal(1790000000002, second.ExchangeTimestamp);
    }

    [Fact]
    public void MissingSequence_FailsClosedAndRequiresFreshSnapshot()
    {
        var book = new DeribitRawBook(Instrument);
        using var snapshot = Frame("""
        {"instrument_name":"ETH-PERPETUAL","type":"snapshot","change_id":10,
         "timestamp":1790000000000,"bids":[["new",100,1]],
         "asks":[["new",101,1]]}
        """);
        using var gap = Frame("""
        {"instrument_name":"ETH-PERPETUAL","type":"change","change_id":13,
         "prev_change_id":12,"timestamp":1790000000010,
         "bids":[["change",100,999]],"asks":[]}
        """);
        Assert.True(book.Apply(snapshot.RootElement, out _));
        Assert.False(book.Apply(gap.RootElement, out var invalid));
        Assert.Equal("sequence_gap", invalid.State);
        Assert.Empty(invalid.Bids);
        Assert.Empty(invalid.Asks);
        Assert.Null(invalid.ChangeId);
        using var orphan = Frame("""
        {"instrument_name":"ETH-PERPETUAL","type":"change","change_id":14,
         "prev_change_id":13,"timestamp":1790000000011,
         "bids":[["change",100,5]],"asks":[]}
        """);
        Assert.False(book.Apply(orphan.RootElement, out _));
        Assert.True(book.Apply(snapshot.RootElement, out _));
    }

    [Fact]
    public void CrossedOrMalformedSnapshotIsNeverPublishedAsSynced()
    {
        var book = new DeribitRawBook(Instrument);
        using var crossed = Frame("""
        {"instrument_name":"ETH-PERPETUAL","type":"snapshot","change_id":200,
         "timestamp":1790000000000,"bids":[["new",2002,1]],
         "asks":[["new",2001,1]]}
        """);
        Assert.False(book.Apply(crossed.RootElement, out var invalid));
        Assert.Equal("crossed", invalid.State);
        Assert.Empty(invalid.Bids);
        Assert.Empty(invalid.Asks);

        using var foreign = Frame("""
        {"instrument_name":"BTC-PERPETUAL","type":"snapshot","change_id":201,
         "timestamp":1790000000001,"bids":[["new",2000,1]],
         "asks":[["new",2001,1]]}
        """);
        Assert.False(book.Apply(foreign.RootElement, out _));
        Assert.Null(book.View("invalid").ChangeId);
    }

    [Fact]
    public void FullL2StoresBeyondVisibleDepthAndPublishesAtMost50Levels()
    {
        var book = new DeribitRawBook(Instrument);
        // 75 authentic price-level triples per side. A full-depth L2 book
        // retains more than the bounded browser view while maintaining best
        // bid and ask ordering and precise per-price amounts.
        var bids = Enumerable.Range(0, 75)
            .Select(i => new object[] { "new", 2000 - i, 1 }).ToArray();
        var asks = Enumerable.Range(0, 75)
            .Select(i => new object[] { "new", 2001 + i, 2 }).ToArray();
        using var frame = Frame(JsonSerializer.Serialize(new {
            instrument_name = Instrument, type = "snapshot", change_id = 500,
            timestamp = 1790000000000L, bids, asks
        }));
        Assert.True(book.Apply(frame.RootElement, out var first));
        Assert.Equal(50, DeribitRawBook.MaxVisibleLevels);
        Assert.Equal(50, first.Bids.Length);
        Assert.Equal(50, first.Asks.Length);
        Assert.Equal(2000m, first.Bids[0].Price);
        Assert.Equal(1951m, first.Bids[49].Price);
        Assert.Equal(2001m, first.Asks[0].Price);
        Assert.Equal(2050m, first.Asks[49].Price);

        var deletes = Enumerable.Range(0, 10)
            .Select(i => new object[] { "delete", 2000 - i, 0 }).ToArray();
        using var delta = Frame(JsonSerializer.Serialize(new {
            instrument_name = Instrument, type = "change", change_id = 501,
            prev_change_id = 500, timestamp = 1790000000001L,
            bids = deletes, asks = Array.Empty<object[]>()
        }));
        Assert.True(book.Apply(delta.RootElement, out var second));
        Assert.Equal(50, second.Bids.Length);
        Assert.Equal(1990m, second.Bids[0].Price);
        Assert.Equal(1941m, second.Bids[49].Price);
        Assert.Equal(501, second.ChangeId);
    }

    [Fact]
    public void RawFeedIsEventDrivenAndLocalWebSocketOnly()
    {
        var root = LocateRoot();
        var feed = File.ReadAllText(Path.Combine(root, "apps", "TradeOps.TerminalApi",
            "Market", "DeribitRawFeedHub.cs"));
        var ui = File.ReadAllText(Path.Combine(root, "apps", "trade-terminal",
            "src", "Terminal.tsx"));
        var endpoint = File.ReadAllText(Path.Combine(root, "apps",
            "TradeOps.TerminalApi", "Market", "MarketStreamEndpoints.cs"));
        var vite = File.ReadAllText(Path.Combine(root, "apps", "trade-terminal",
            "vite.config.ts"));
        Assert.Contains("\"book.\" + _name + \".raw\"", feed);
        Assert.Contains("\"trades.\" + _name + \".raw\"", feed);
        Assert.Contains("\"ticker.\" + _name + \".raw\"", feed);
        Assert.Contains("prev_change_id", File.ReadAllText(Path.Combine(root,
            "apps", "TradeOps.TerminalApi", "Market", "DeribitRawBook.cs")));
        Assert.Contains("Channel.CreateBounded", feed);
        Assert.Contains("BoundedChannelFullMode.DropOldest", feed);
        Assert.Contains("wss://", feed);
        Assert.Contains("requestAnimationFrame(renderLatest)", ui);
        Assert.Contains("new WebSocket(url)", ui);
        Assert.DoesNotContain("/market/orderbook?instrument=", ui);
        Assert.DoesNotContain("/market/trades?instrument=", ui);
        Assert.Contains("http://127.0.0.1:5173", endpoint);
        Assert.Contains("ws: true", vite);
        Assert.Contains("proxyReqWs", vite);
        Assert.DoesNotContain("private/buy", feed);
        Assert.DoesNotContain("private/sell", feed);
        Assert.DoesNotContain("localStorage", ui);
    }

    private static string LocateRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TradeOps.sln"))) return d.FullName;
        throw new InvalidOperationException("TradeOps root missing.");
    }
}
