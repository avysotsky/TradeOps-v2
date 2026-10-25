using System.Text.Json;
using TradeOps.TerminalApi.Market;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitRawBookTests
{
    private const string Instrument = "ETH-PERPETUAL";

    private static JsonDocument Frame(string data) => JsonDocument.Parse(data);

    [Fact]
    public void SelectedDepthMapsToOnlyActualDeribitSubscriptions()
    {
        foreach (var depth in new[] { 1, 10, 20 })
        {
            Assert.True(DeribitBookSubscription.ValidDepth(depth));
            Assert.True(DeribitBookSubscription.TryParseMode(depth.ToString(), out var parsed));
            Assert.Equal(depth, parsed);
            Assert.Equal($"book.ETH-PERPETUAL.none.{depth}.100ms",
                DeribitBookSubscription.Channel(Instrument, depth));
            Assert.True(DeribitBookSubscription.IsAggregated(depth));
        }

        Assert.True(DeribitBookSubscription.TryParseMode("raw", out var raw));
        Assert.Equal(DeribitBookSubscription.RawMode, raw);
        Assert.Equal("book.ETH-PERPETUAL.raw",
            DeribitBookSubscription.Channel(Instrument, raw));
        Assert.False(DeribitBookSubscription.IsAggregated(raw));
        Assert.Equal("raw", DeribitBookSubscription.PublicMode(raw));

        foreach (var invalid in new[] { "0", "25", "50", "-1", "RAW", "10x", "" })
            Assert.False(DeribitBookSubscription.TryParseMode(invalid, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DeribitBookSubscription.Channel(Instrument, 25));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(20)]
    public void GroupedSnapshotAcceptanceUsesNativeExchangeDepth(int depth)
    {
        var book = new DeribitRawBook(Instrument);
        using var snapshot = Frame("""
        {"instrument_name":"ETH-PERPETUAL","timestamp":1790000000000,
         "bids":[[2000,5]],"asks":[[2001,7]]}
        """);
        Assert.True(book.ApplyGroupedSnapshot(snapshot.RootElement, depth, out var view));
        Assert.Single(view.Bids);
        Assert.Single(view.Asks);
    }

    [Fact]
    public void GroupedDepth10SnapshotsReplaceRatherThanMergeOldPriceLevels()
    {
        var book = new DeribitRawBook(Instrument);
        using var first = Frame("""
        {"instrument_name":"ETH-PERPETUAL","type":"snapshot","change_id":100,
         "timestamp":1790000000000,
         "bids":[[2000,5],[1999,3]],"asks":[[2001,7],[2002,2]]}
        """);
        Assert.True(book.ApplyGroupedSnapshot(first.RootElement, 10, out var initial));
        Assert.Equal("synced", initial.State);
        Assert.Equal(2, initial.Bids.Length);
        Assert.Equal(2, initial.Asks.Length);

        // Grouped messages are standalone depth snapshots. No prev_change_id.
        using var second = Frame("""
        {"instrument_name":"ETH-PERPETUAL","type":"change","change_id":150,
         "timestamp":1790000000100,
         "bids":[[1998,8]],"asks":[[2003,4]]}
        """);
        Assert.True(book.ApplyGroupedSnapshot(second.RootElement, 10, out var next));
        Assert.Single(next.Bids);
        Assert.Single(next.Asks);
        Assert.Equal(1998m, next.Bids[0].Price);
        Assert.Equal(2003m, next.Asks[0].Price);
        Assert.Equal(150, next.ChangeId);
    }

    [Fact]
    public void GroupedDepth10RejectsMalformedOrCrossedDataFailClosed()
    {
        var book = new DeribitRawBook(Instrument);
        using var duplicate = Frame("""
        {"instrument_name":"ETH-PERPETUAL","timestamp":1790000000100,
         "bids":[[2000,1],[2000,2]],"asks":[[2001,1]]}
        """);
        Assert.False(book.ApplyGroupedSnapshot(duplicate.RootElement, 10, out var invalid));
        Assert.Equal("L2_GROUPED_MALFORMED", book.LastFailure?.Code);
        Assert.Empty(invalid.Bids);

        using var crossed = Frame("""
        {"instrument_name":"ETH-PERPETUAL","timestamp":1790000000200,
         "bids":[[2001,1]],"asks":[[2000,1]]}
        """);
        Assert.False(book.ApplyGroupedSnapshot(crossed.RootElement, 10, out _));
        Assert.Equal("L2_CROSSED", book.LastFailure?.Code);

        using var good = Frame("""
        {"instrument_name":"ETH-PERPETUAL","timestamp":1790000000300,
         "bids":[["new",1999,1]],"asks":[["new",2000,1]]}
        """);
        Assert.True(book.ApplyGroupedSnapshot(good.RootElement, 10, out var recovered));
        Assert.Single(recovered.Bids);
    }

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
        Assert.Equal("L2_PREV_CHANGE_MISMATCH", book.LastFailure?.Code);
        Assert.Equal(10, book.LastFailure?.ExpectedChangeId);
        Assert.Equal(12, book.LastFailure?.PrevChangeId);
        Assert.Equal(13, book.LastFailure?.ChangeId);
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
        Assert.Equal("L2_CROSSED", book.LastFailure?.Code);
        Assert.Equal(200, book.LastFailure?.ChangeId);
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
    public void InvalidJsonFieldTypesFailClosedWithSanitizedCodeInsteadOfThrowing()
    {
        var book = new DeribitRawBook(Instrument);
        using var valid = Frame("""
            {"instrument_name":"ETH-PERPETUAL","type":"snapshot","change_id":60,
             "timestamp":1790000000000,"bids":[["new",100,1]],
             "asks":[["new",101,1]]}
            """);
        Assert.True(book.Apply(valid.RootElement, out _));
        using var malformed = Frame("""
            {"instrument_name":123,"type":"change","change_id":61,
             "prev_change_id":60,"timestamp":1790000000002,
             "bids":[],"asks":[]}
            """);
        Assert.False(book.Apply(malformed.RootElement, out var invalid));
        Assert.Equal("L2_MALFORMED", book.LastFailure?.Code);
        Assert.Equal(60, book.LastFailure?.ExpectedChangeId);
        Assert.Equal(61, book.LastFailure?.ChangeId);
        Assert.Empty(invalid.Asks);
        Assert.Empty(invalid.Bids);
    }

    [Fact]
    public void ReorderedOrDuplicateChangeIdsMustNotSilentlyAdvanceBook()
    {
        var book = new DeribitRawBook(Instrument);
        using var snapshot = Frame("""
            {"instrument_name":"ETH-PERPETUAL","type":"snapshot","change_id":1000,
             "timestamp":1790000000000,"bids":[["new",100,1]],
             "asks":[["new",101,1]]}
            """);
        Assert.True(book.Apply(snapshot.RootElement, out _));
        using var duplicate = Frame("""
            {"instrument_name":"ETH-PERPETUAL","type":"change","change_id":1000,
             "prev_change_id":1000,"timestamp":1790000000001,
             "bids":[["change",100,500]],"asks":[]}
            """);
        Assert.False(book.Apply(duplicate.RootElement, out var invalid));
        Assert.Equal("L2_PREV_CHANGE_MISMATCH", book.LastFailure?.Code);
        Assert.Empty(invalid.Bids);
        Assert.True(book.Apply(snapshot.RootElement, out _));
        Assert.Null(book.LastFailure);
        using var skippedNumbersButCorrectLink = Frame("""
            {"instrument_name":"ETH-PERPETUAL","type":"change","change_id":2040,
             "prev_change_id":1000,"timestamp":1790000000002,
             "bids":[["change",100,2]],"asks":[]}
            """);
        Assert.True(book.Apply(skippedNumbersButCorrectLink.RootElement, out var result));
        Assert.Equal(2040, result.ChangeId);
        Assert.Equal(2m, result.Bids[0].Amount);
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
    public void StandaloneTerminalApiHasNoSecondGlobalProgramEntryPoint()
    {
        // The shared test project references both TradeOps.Api and TerminalApi.
        // A second global Program type makes unrelated WebApplicationFactory
        // integration tests fail to compile (CS0433).
        var terminalAssembly = typeof(DeribitRawBook).Assembly;
        Assert.Null(terminalAssembly.GetType("Program"));
        Assert.NotNull(terminalAssembly.GetType(
            "TradeOps.TerminalApi.TerminalApiEntryPoint"));
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
        Assert.Contains("_bookChannel", feed);
        Assert.Contains("DeribitBookSubscription.Channel", feed);
        Assert.Contains("DeribitBookSubscription.IsAggregated", feed);
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
