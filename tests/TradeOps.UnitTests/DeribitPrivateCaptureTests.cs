using System.Net.WebSockets;
using TradeOps.Application.Oms;
using TradeOps.Connectors.Deribit;
using Xunit;
using static TradeOps.UnitTests.Support.DeribitCaptureTestRig;

namespace TradeOps.UnitTests;

public sealed class DeribitPrivateCaptureTests
{
    [Fact]
    public async Task AuthenticationRpcError_IsTerminalAndDoesNotExposeServerPayload()
    {
        var socket = new Socket { OnRequest = (s, method, id) =>
        {
            if (method == "public/auth") s.Emit(System.Text.Json.JsonSerializer.Serialize(new
            { jsonrpc = "2.0", id, error = new { code = 13004, message = "synthetic-private-account" } }));
        } };
        await using var coordinator = new OmsPrivateCaptureCoordinator(new Capture());
        var session = await coordinator.SwitchAsync(Scope());
        var failure = await Assert.ThrowsAsync<DeribitCaptureException>(() =>
            Stream(() => socket).RunCaptureAsync(coordinator, session, Plan()));
        Assert.Equal(OmsPrivateStreamProblem.AuthenticationRejected, failure.Problem);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("synthetic-private", failure.Message);
        Assert.DoesNotContain(socket.Sent, x => x.Method.StartsWith("private/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainnetCapturePath_IsReadOnly_AndVerifiesMainOrSubaccount(bool sub)
    {
        var socket = new Socket { AccountType = sub ? "subaccount" : "main", OnSubscribe = (s, id, p) =>
        { s.Emit(Trade()); s.Reply(id, p.GetProperty("channels")); } };
        var capture = new Capture();
        await using var coordinator = new OmsPrivateCaptureCoordinator(capture);
        var scope = new TradeOps.Domain.Oms.AccountKey(new("tenant"), new("Deribit"),
            TradeOps.Domain.Oms.OmsEnvironment.Mainnet, new("account"), sub ? new TradeOps.Domain.Oms.TradingSubaccountId("sub") : null);
        var session = await coordinator.SwitchAsync(scope);
        using var stop = new CancellationTokenSource();
        var run = Stream(() => socket, Options("wss://www.deribit.com/ws/api/v2"))
            .RunCaptureAsync(coordinator, session, Plan(sub: sub), stop.Token);
        try
        {
            Assert.Equal(scope, (await capture.Next()).Scope);
            Assert.Equal("www.deribit.com", socket.Endpoint!.Host);
            Assert.All(socket.Sent, x => Assert.Contains(x.Method, new[] { "public/auth", "private/get_account_summary", "public/set_heartbeat", "private/subscribe", "public/test" }));
            Assert.Equal(OmsPrivateStreamPhase.Recovering, coordinator.Status!.Phase);
        }
        finally { stop.Cancel(); await run.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task CaptureFailure_IsSanitized_AndStopsTransportWithoutReportingCommit()
    {
        var socket = new Socket { OnSubscribe = (s, id, p) => { s.Emit(Trade()); s.Reply(id, p.GetProperty("channels")); } };
        var capture = new Capture { BeforeCommit = (_, _) => throw new InvalidOperationException("synthetic-private-payload") };
        await using var coordinator = new OmsPrivateCaptureCoordinator(capture);
        var session = await coordinator.SwitchAsync(Scope());
        var failure = await Assert.ThrowsAsync<DeribitCaptureException>(() =>
            Stream(() => socket).RunCaptureAsync(coordinator, session, Plan()).WaitAsync(Deadline));
        Assert.Equal(OmsPrivateStreamProblem.CaptureFailed, failure.Problem);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("synthetic-private", failure.Message);
        Assert.Empty(capture.Events);
        Assert.True(socket.Aborted);
    }

    [Fact]
    public async Task IncompleteSubscribeAck_DoesNotReleaseBufferedEvents()
    {
        var first = new Socket { OnSubscribe = (s, id, _) => { s.Emit(Trade()); s.Reply(id, Array.Empty<string>()); } };
        var second = new Socket { HoldAuth = true };
        var sockets = new Queue<Socket>(new[] { first, second });
        var capture = new Capture();
        await using var coordinator = new OmsPrivateCaptureCoordinator(capture);
        var session = await coordinator.SwitchAsync(Scope());
        using var stop = new CancellationTokenSource();
        var run = Stream(() => sockets.Dequeue()).RunCaptureAsync(coordinator, session, Plan(), stop.Token);
        try
        {
            await second.AuthSent.Task.WaitAsync(Deadline);
            Assert.Equal(OmsPrivateStreamProblem.ProtocolViolation, coordinator.Status!.LastFailure);
            Assert.Empty(capture.Events);
        }
        finally { stop.Cancel(); await run.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task MissingTestRequestReply_HasBoundedDeadlineAndReconnect()
    {
        var first = new Socket { IgnoreTest = true };
        var second = new Socket { HoldAuth = true };
        var sockets = new Queue<Socket>(new[] { first, second });
        await using var coordinator = new OmsPrivateCaptureCoordinator(new Capture());
        var session = await coordinator.SwitchAsync(Scope());
        using var stop = new CancellationTokenSource();
        var run = Stream(() => sockets.Dequeue()).RunCaptureAsync(coordinator, session, Plan(limits: new()
        { RpcTimeout = TimeSpan.FromMilliseconds(100), ReceiveTimeout = TimeSpan.FromSeconds(5),
          ReconnectMinimum = TimeSpan.Zero, ReconnectMaximum = TimeSpan.Zero }), stop.Token);
        try
        {
            await first.Subscribed.Task.WaitAsync(Deadline);
            first.Heartbeat();
            await second.AuthSent.Task.WaitAsync(Deadline);
            Assert.True(first.Aborted);
            Assert.NotNull(coordinator.Status!.LastFailure);
        }
        finally { stop.Cancel(); await run.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task HandshakeEventsAndHeartbeat_AreNotLost_AndRemainRecoveringAfterCommit()
    {
        var socket = new Socket { OnSubscribe = (s, id, p) => { s.Heartbeat(); s.Emit(Trade()); s.Reply(id, p.GetProperty("channels")); } };
        var capture = new Capture();
        await using var coordinator = new OmsPrivateCaptureCoordinator(capture);
        var session = await coordinator.SwitchAsync(Scope());
        using var stop = new CancellationTokenSource();
        var run = Stream(() => socket).RunCaptureAsync(coordinator, session, Plan(), stop.Token);
        try
        {
            var item = await capture.Next();
            await socket.Tested.Task.WaitAsync(Deadline);
            Assert.Equal(session.Scope, item.Scope);
            Assert.Equal(10m, item.Facts.NativeAmount);
            Assert.Equal(OmsPrivateStreamPhase.Recovering, coordinator.Status!.Phase);
            Assert.Contains(socket.Sent, x => x.Method == "public/auth" && x.Parameters.GetProperty("scope").GetString()!.Contains("trade:read"));
            Assert.DoesNotContain(socket.Sent, x => x.Method == "private/subscribe" && x.Parameters.TryGetProperty("access_token", out _));
        }
        finally { stop.Cancel(); await run.WaitAsync(Deadline); }
        Assert.True(socket.Aborted);
    }

    [Theory]
    [InlineData("connection mainaccount trade:read_write account:read wallet:none block_trade:none block_rfq:none")]
    [InlineData("connection mainaccount trade:read account:read wallet:read block_trade:none block_rfq:none")]
    [InlineData("session:other trade:read account:read wallet:none block_trade:none block_rfq:none")]
    [InlineData("connection mainaccount trade:read trade:read account:read wallet:none block_trade:none block_rfq:none")]
    public async Task UnverifiedPermissions_FailClosedBeforeSubscriptionOrCapture(string scope)
    {
        var socket = new Socket { GrantedScope = scope };
        var capture = new Capture();
        await using var coordinator = new OmsPrivateCaptureCoordinator(capture);
        var session = await coordinator.SwitchAsync(Scope());
        var failure = await Assert.ThrowsAsync<DeribitCaptureException>(() => Stream(() => socket).RunCaptureAsync(coordinator, session, Plan()));
        Assert.Equal(OmsPrivateStreamProblem.AuthenticationRejected, failure.Problem);
        Assert.Empty(capture.Events);
        Assert.DoesNotContain(socket.Sent, x => x.Method.StartsWith("private/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WrongAuthenticatedAccount_FailsClosedBeforeSubscription()
    {
        var socket = new Socket { AccountId = 999 };
        await using var coordinator = new OmsPrivateCaptureCoordinator(new Capture());
        var session = await coordinator.SwitchAsync(Scope());
        var failure = await Assert.ThrowsAsync<DeribitCaptureException>(() => Stream(() => socket).RunCaptureAsync(coordinator, session, Plan()));
        Assert.Equal(OmsPrivateStreamProblem.AccountMismatch, failure.Problem);
        Assert.DoesNotContain(socket.Sent, x => x.Method == "private/subscribe");
    }

    [Theory]
    [InlineData("wss://www.deribit.com/ws/api/v2")]
    [InlineData("wss://test.deribit.com:444/ws/api/v2")]
    [InlineData("wss://test.deribit.com/ws/api/v2?x=1")]
    public async Task EndpointMustMatchFullBinding_BeforeCreatingAnySocket(string url)
    {
        var called = false;
        await using var coordinator = new OmsPrivateCaptureCoordinator(new Capture());
        var session = await coordinator.SwitchAsync(Scope());
        await Assert.ThrowsAsync<ArgumentException>(() => Stream(() => { called = true; return new Socket(); }, Options(url))
            .RunCaptureAsync(coordinator, session, Plan()));
        Assert.False(called);
    }

    [Fact]
    public async Task SlowCapture_DoesNotBlockHeartbeatReader()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new Capture { BeforeCommit = async (_, ct) =>
        { entered.TrySetResult(); await release.Task.WaitAsync(ct); return OmsPrivateCaptureResult.Inserted; } };
        var socket = new Socket { OnSubscribe = (s, id, p) => { s.Emit(Trade()); s.Reply(id, p.GetProperty("channels")); } };
        await using var coordinator = new OmsPrivateCaptureCoordinator(capture);
        var session = await coordinator.SwitchAsync(Scope());
        using var stop = new CancellationTokenSource();
        var run = Stream(() => socket).RunCaptureAsync(coordinator, session, Plan(), stop.Token);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            socket.Heartbeat();
            await socket.Tested.Task.WaitAsync(Deadline);
            Assert.Empty(capture.Events);
            release.TrySetResult();
            await capture.Next();
        }
        finally { release.TrySetResult(); stop.Cancel(); await run.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task QueueOverflow_IsExplicitGapAndReconnect_NoReadyOrUnverifiedCommits()
    {
        var first = new Socket { OnSubscribe = (s, id, p) =>
        { s.Emit(Trade(tradeId: "one")); s.Emit(Trade(tradeId: "two")); s.Reply(id, p.GetProperty("channels")); } };
        var second = new Socket { HoldAuth = true };
        var sockets = new Queue<Socket>(new[] { first, second });
        var capture = new Capture();
        await using var coordinator = new OmsPrivateCaptureCoordinator(capture);
        var session = await coordinator.SwitchAsync(Scope());
        using var stop = new CancellationTokenSource();
        var run = Stream(() => sockets.Dequeue()).RunCaptureAsync(coordinator, session, Plan(limits: new()
        { BufferCapacity = 1, ReconnectMinimum = TimeSpan.Zero, ReconnectMaximum = TimeSpan.Zero }), stop.Token);
        try
        {
            await second.AuthSent.Task.WaitAsync(Deadline);
            Assert.Equal(OmsPrivateStreamProblem.BufferOverflow, coordinator.Status!.LastFailure);
            Assert.Empty(capture.Events);
            Assert.True(first.Aborted);
        }
        finally { stop.Cancel(); await run.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task Disconnect_ReconnectUsesNewEpochAndKeepsStableExecutionIdentity()
    {
        var first = new Socket(); var second = new Socket();
        var sockets = new Queue<Socket>(new[] { first, second });
        foreach (var socket in sockets) socket.OnSubscribe = (s, id, p) => { s.Emit(Trade()); s.Reply(id, p.GetProperty("channels")); };
        var capture = new Capture();
        await using var coordinator = new OmsPrivateCaptureCoordinator(capture);
        var session = await coordinator.SwitchAsync(Scope());
        using var stop = new CancellationTokenSource();
        var run = Stream(() => sockets.Dequeue()).RunCaptureAsync(coordinator, session, Plan(), stop.Token);
        try
        {
            var a = await capture.Next();
            first.Fail(new WebSocketException("synthetic transport failure"));
            var b = await capture.Next();
            Assert.NotEqual(a.Observation.StreamEpoch, b.Observation.StreamEpoch);
            Assert.Equal(OmsPrivateEventCanonical.Encode(a).EventId, OmsPrivateEventCanonical.Encode(b).EventId);
            Assert.Equal(OmsPrivateEventCanonical.Encode(a).Json, OmsPrivateEventCanonical.Encode(b).Json);
            Assert.Equal(OmsPrivateStreamPhase.Recovering, coordinator.Status!.Phase);
            Assert.Equal(OmsPrivateStreamProblem.TransportLost, coordinator.Status.LastFailure);
        }
        finally { stop.Cancel(); await run.WaitAsync(Deadline); }
    }

    [Fact]
    public async Task RenewalAndSilenceDeadline_ReconnectWithoutDeclaringReady()
    {
        foreach (var renewal in new[] { false, true })
        {
            var first = new Socket(); var second = new Socket { HoldAuth = true };
            var sockets = new Queue<Socket>(new[] { first, second });
            await using var coordinator = new OmsPrivateCaptureCoordinator(new Capture());
            var session = await coordinator.SwitchAsync(Scope());
            using var stop = new CancellationTokenSource();
            var run = Stream(() => sockets.Dequeue()).RunCaptureAsync(coordinator, session, Plan(limits: new()
            { ReceiveTimeout = renewal ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100),
              MaxConnectionLifetime = renewal ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMinutes(1),
              ReconnectMinimum = TimeSpan.Zero, ReconnectMaximum = TimeSpan.Zero }), stop.Token);
            try
            {
                await second.AuthSent.Task.WaitAsync(Deadline);
                Assert.Equal(renewal ? OmsPrivateStreamProblem.Renewal : OmsPrivateStreamProblem.TransportLost,
                    coordinator.Status!.LastFailure);
            }
            finally { stop.Cancel(); await run.WaitAsync(Deadline); }
        }
    }

    [Fact]
    public async Task AccountSwitch_AbortsOldStream_AndRunnerOwnershipExcludesLegacyAndDuplicateRuns()
    {
        var first = new Socket { HoldAuth = true };
        var second = new Socket { AccountId = 456, OnSubscribe = (s, id, p) => { s.Emit(Trade()); s.Reply(id, p.GetProperty("channels")); } };
        var sockets = new Queue<Socket>(new[] { first, second });
        var capture = new Capture();
        await using var coordinator = new OmsPrivateCaptureCoordinator(capture);
        var old = await coordinator.SwitchAsync(Scope());
        var adapter = Stream(() => sockets.Dequeue());
        using var stop = new CancellationTokenSource();
        var oldRun = adapter.RunCaptureAsync(coordinator, old, Plan(), stop.Token);
        await first.AuthSent.Task.WaitAsync(Deadline);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.RunAsync((_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Stream(() => new Socket()).RunCaptureAsync(coordinator, old, Plan()));
        var current = await coordinator.SwitchAsync(Scope("other"));
        await oldRun.WaitAsync(Deadline);
        var run = adapter.RunCaptureAsync(coordinator, current, Plan(account: 456), stop.Token);
        try
        {
            var item = await capture.Next();
            Assert.Equal(current.Scope, item.Scope);
            Assert.True(first.Aborted);
            Assert.Equal(current.Scope, coordinator.Status!.Scope);
            Assert.Single(capture.Events);
        }
        finally { stop.Cancel(); await run.WaitAsync(Deadline); }
    }
}
