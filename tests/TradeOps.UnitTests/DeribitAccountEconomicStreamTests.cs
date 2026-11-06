using System.Text.Json;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;

public sealed class DeribitAccountEconomicStreamTests
{
    [Fact]
    public async Task CashChangeRevokesLiveRevisionButMarkOnlyPortfolioUpdatesDoNot()
    {
        await using var h = await Harness.Open();
        h.Socket.Emit(Portfolio(1, 2)); await h.Fence();
        var (revision, window, cut) = await h.Revision();
        h.Socket.Emit(Portfolio(1, 3)); await h.Fence(); Assert.True(h.Coordinator.IsAccountRevisionCurrent(revision));
        h.Socket.Emit(Portfolio(1.1m, 3)); await h.Fence(); Assert.False(h.Coordinator.IsAccountRevisionCurrent(revision));
        Assert.Contains(OmsPrivateReplayProblem.AccountMutation, (await h.Coordinator.ReadReplayAsync(cut)).Problems);
        Assert.True(h.Coordinator.ValidateAccountSubscriptions(h.Subscriptions)); Assert.Empty(h.Capture.Events);
        h.Coordinator.AbandonReplay(window);
        var (next, _, _) = await h.Revision(); Assert.True(h.Coordinator.IsAccountRevisionCurrent(next));
        Assert.True(next.EconomicGeneration > revision.EconomicGeneration); Assert.Equal(1, h.Socket.Connections);
    }

    [Fact]
    public async Task PositionDeliveryWithoutTradeRevokesRevisionButMarkDerivedBaseSizeDoesNot()
    {
        await using var h = await Harness.Open();
        h.Socket.Emit(Position(10, 0.0002m)); await h.Fence();
        var (revision, _, _) = await h.Revision();
        h.Socket.Emit(Position(10, 0.0001m)); await h.Fence(); Assert.True(h.Coordinator.IsAccountRevisionCurrent(revision));
        h.Socket.Emit(Position(0, 0)); await h.Fence(); Assert.False(h.Coordinator.IsAccountRevisionCurrent(revision));
        Assert.Empty(h.Capture.Events); Assert.True(h.Coordinator.ValidateAccountSubscriptions(h.Subscriptions));
    }

    private static string Portfolio(decimal cash, decimal equity) => JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "subscription",
        @params = new { channel = "user.portfolio.any", data = new { currency = "BTC", balance = cash, equity, fee_balance = 0,
            locked_balance = 0, cross_collateral_enabled = false, portfolio_margining_enabled = false, margin_model = "segregated_sm" } } });
    private static string Position(decimal size, decimal baseSize) => JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "subscription",
        @params = new { channel = "user.changes.any.any.raw", data = new { orders = Array.Empty<object>(), trades = Array.Empty<object>(),
            positions = new[] { new { instrument_name = "BTC-PERPETUAL", kind = "future", user_id = 123,
                direction = size == 0 ? "zero" : "buy", size, size_currency = baseSize } } } } });

    private sealed class Harness : IAsyncDisposable
    {
        internal readonly Socket Socket = new(); internal readonly Capture Capture = new();
        internal readonly DeribitPrivateCaptureDiagnostics Diagnostics = new();
        internal OmsPrivateCaptureCoordinator Coordinator = null!; internal OmsPrivateCaptureSession Session = null!;
        internal OmsPrivateAccountSubscriptions Subscriptions = null!;
        private readonly CancellationTokenSource stop = new(); private Task run = null!; private int fences;
        internal static async Task<Harness> Open()
        {
            var h = new Harness(); h.Coordinator = new(h.Capture, new()); h.Session = await h.Coordinator.SwitchAsync(Scope());
            var plan = DeribitPrivateCapturePlan.ForAccountRecovery(123, false, Plan().Instruments.Values, Plan().Limits);
            h.run = Stream(() => h.Socket).RunCaptureAsync(h.Coordinator, h.Session, plan, h.stop.Token, h.Diagnostics);
            using var deadline = new CancellationTokenSource(Deadline);
            while (h.Coordinator.Status?.Phase != OmsPrivateStreamPhase.Recovering) await Task.Delay(5, deadline.Token);
            h.Subscriptions = h.Coordinator.ReadAccountSubscriptions(h.Session, h.Coordinator.Status.Epoch)!;
            Assert.Equal(OmsAccountSubscriptionProfile.EconomicAccountV2, h.Subscriptions.Profile); return h;
        }
        internal async Task Fence()
        {
            var expected = ++fences; Socket.Heartbeat(); using var deadline = new CancellationTokenSource(Deadline);
            while (Diagnostics.Snapshot.HeartbeatReplies < expected) await Task.Delay(5, deadline.Token);
        }
        internal async Task<(OmsPrivateAccountRevision, OmsPrivateReplayWindow, OmsPrivateReplayCut)> Revision()
        {
            var window = await Coordinator.BeginReplayAsync(Session, Subscriptions.Epoch); var cut = await Coordinator.SealReplayAsync(window);
            var revision = Coordinator.TryCreateAccountRevision(cut, Subscriptions); Assert.NotNull(revision); return (revision, window, cut);
        }
        public async ValueTask DisposeAsync()
        { stop.Cancel(); try { await run.WaitAsync(Deadline); } finally { await Coordinator.DisposeAsync(); stop.Dispose(); } }
    }
}
