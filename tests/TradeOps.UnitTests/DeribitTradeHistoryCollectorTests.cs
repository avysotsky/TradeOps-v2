using TradeOps.Infrastructure.Exchange.Deribit;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class DeribitTradeHistoryCollectorTests
{
    private static DeribitTradeEvidence Trade(string id, long ts,
        decimal price = 50000m) =>
        new(id, "ORDER-" + id, "BTC-PERPETUAL", "buy",
            100m, price, 0.0001m, "BTC", ts, 100m);

    private static DeribitTradeEvidencePage Page(string currency,
        long start, long end, bool historical, bool hasMore,
        params DeribitTradeEvidence[] trades) =>
        new(currency, start, end, historical, hasMore, trades);

    [Fact]
    public async Task NoMore_ReturnsBoundedAggregates_WithoutClaimingHistoryComplete()
    {
        var calls = 0;
        var snapshot = await DeribitTradeHistoryCollector.CollectAsync(
            "BTC", 100, 500, true, (c, s, e, h, _) =>
            {
                calls++;
                return Task.FromResult(Page(c, s, e, h, false,
                    Trade("t1", 120), Trade("t2", 490)));
            });
        Assert.Equal(2, snapshot.UniqueTrades);
        Assert.Equal(1, snapshot.PageCalls);
        Assert.Equal(1, calls);
        Assert.Equal(0, snapshot.DuplicateBoundaryTrades);
        Assert.True(snapshot.ApiPagesExhausted);
        Assert.False(snapshot.HistoricalCoverageProven);
    }

    [Fact]
    public async Task HasMore_SplitsInclusiveWindowsAndDeduplicatesSameTradeAcrossBoundary()
    {
        var scopes = new List<(long, long)>();
        var evidence = await DeribitTradeHistoryCollector.CollectAsync(
            "BTC", 100, 200, false, (currency, start, end, hist, _) =>
            {
                scopes.Add((start, end));
                if (start == 100 && end == 200)
                    return Task.FromResult(Page(currency, start, end,
                        hist, true, Trade("t1", 150)));

                if (start == 100 && end == 150)
                    return Task.FromResult(Page(currency, start, end,
                        hist, false, Trade("t1", 150)));

                return Task.FromResult(Page(currency, start, end,
                    hist, false, Trade("t1", 150), Trade("t2", 180)));
            });

        Assert.Equal(3, evidence.PageCalls);
        Assert.Equal(2, evidence.UniqueTrades);
        Assert.Equal(2, evidence.DuplicateBoundaryTrades);
        Assert.Contains((100, 150), scopes);
        Assert.Contains((150, 200), scopes);
    }

    [Fact]
    public async Task SharedTradeIdWithDifferentData_StopsInsteadOfSilentlyOverwriting()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeribitTradeHistoryCollector.CollectAsync(
                "BTC", 100, 200, false, (ccy, start, end, hist, _) =>
                    Task.FromResult(
                        start == 100 && end == 200
                            ? Page(ccy, start, end, hist, true,
                                Trade("t1", 150))
                            : Page(ccy, start, end, hist, false,
                                Trade("t1", 150, 99999m)))));
    }

    [Fact]
    public async Task OverflowInSingleMillisecond_FailsClosed()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeribitTradeHistoryCollector.CollectAsync(
                "BTC", 100, 100, true, (ccy, start, end, hist, _) =>
                    Task.FromResult(Page(ccy, start, end, hist, true,
                        Trade("t1", 100)))));
    }

    [Fact]
    public async Task MaxRequestsBudget_IsEnforced()
    {
        var count = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeribitTradeHistoryCollector.CollectAsync(
                "BTC", 100, 100_000, true, (ccy, start, end, hist, _) =>
                {
                    count++;
                    return Task.FromResult(Page(ccy, start, end,
                        hist, true, Trade("t" + count, start)));
                }));
        Assert.True(count <= DeribitTradeHistoryCollector.MaximumCallsPerMode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../BTC")]
    [InlineData("btc")]
    [InlineData("BTC ETH")]
    [InlineData("BTC?sort=desc")]
    public async Task InvalidCurrency_IsRejectedBeforeCallingRemote(string currency)
    {
        var sent = 0;
        await Assert.ThrowsAsync<ArgumentException>(() =>
            DeribitTradeHistoryCollector.CollectAsync(
                currency, 100, 200, false, (_, _, _, _, _) =>
                {
                    sent++;
                    throw new Exception("Unexpected send");
                }));
        Assert.Equal(0, sent);
    }

    [Fact]
    public async Task WindowBeyond30Days_IsRejectedBeforeNetwork()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            DeribitTradeHistoryCollector.CollectAsync(
                "ETH", 100, 100 + DeribitTradeHistoryCollector.MaximumWindowMs + 1,
                true, (_, _, _, _, _) =>
                    throw new Exception("Unexpected send")));
    }

    [Fact]
    public async Task CallbackCannotSubstituteOtherCurrencyHistoricalModeOrWindow()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeribitTradeHistoryCollector.CollectAsync(
                "BTC", 100, 200, false, (_, _, _, _, _) =>
                    Task.FromResult(Page("ETH", 100, 200, true, false))));
    }

    [Fact]
    public async Task CallbackCannotSupplyOutOfWindowTrade()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeribitTradeHistoryCollector.CollectAsync(
                "BTC", 100, 200, false, (ccy, start, end, historic, _) =>
                    Task.FromResult(Page(ccy, start, end, historic, false,
                        Trade("t1", 201)))));
    }

    [Fact]
    public async Task Cancellation_StopsBeforeFirstRequest()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sent = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DeribitTradeHistoryCollector.CollectAsync(
                "BTC", 100, 200, false, (ccy, start, end, hist, _) =>
                {
                    sent++;
                    return Task.FromResult(Page(ccy, start, end, hist, false));
                }, cts.Token));
        Assert.Equal(0, sent);
    }
}
