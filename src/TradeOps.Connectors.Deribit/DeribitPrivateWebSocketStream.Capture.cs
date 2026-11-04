using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradeOps.Application.Oms;

namespace TradeOps.Connectors.Deribit;

public sealed partial class DeribitPrivateWebSocketStream
{
    private readonly Func<IDeribitPrivateSocket> captureSocket = () => new DeribitPrivateSocket();
    private readonly TimeProvider captureClock = TimeProvider.System;
    private int running;

    internal DeribitPrivateWebSocketStream(DeribitOptions options, ILogger<DeribitPrivateWebSocketStream> logger,
        Func<IDeribitPrivateSocket> socketFactory, TimeProvider? clock = null) : this(options, logger)
    {
        captureSocket = socketFactory;
        captureClock = clock ?? TimeProvider.System;
    }

    private void EnterRun()
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
            throw new InvalidOperationException("This adapter already owns a private stream.");
    }
    private void ExitRun() => Volatile.Write(ref running, 0);

    /// <summary>
    /// Explicit read-only capture path on the existing adapter. Not auto-registered in the legacy worker.
    /// All successful subscriptions remain RECOVERING until a separately qualified B4 coherence gate.
    /// </summary>
    public async Task RunCaptureAsync(OmsPrivateCaptureCoordinator coordinator, OmsPrivateCaptureSession session,
        DeribitPrivateCapturePlan plan, CancellationToken cancellationToken = default,
        DeribitPrivateCaptureDiagnostics? diagnostics = null)
    {
        var endpoint = plan.Validate(options, session.Scope);
        using var owner = session.AcquireRunner();
        EnterRun();
        Guid epoch = default;
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Cancellation);
            var backoff = plan.Limits.ReconnectMinimum;
            while (!lifetime.IsCancellationRequested)
            {
                epoch = await coordinator.BeginEpochAsync(session, lifetime.Token);
                diagnostics?.Epoch();
                try
                {
                    using var socket = captureSocket();
                    await new CaptureEpoch(socket, options, endpoint, plan, coordinator, session, epoch, captureClock, diagnostics)
                        .RunAsync(lifetime.Token);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (DeribitCaptureException failure)
                {
                    diagnostics?.Problem(failure.Problem);
                    await coordinator.UpdateAsync(session, epoch, OmsPrivateStreamPhase.Recovering, failure.Problem);
                    logger.LogWarning("Deribit private capture requires reconciliation: {Reason}", failure.Problem);
                    if (!failure.Retryable) throw;
                }
                await Task.Delay(backoff, captureClock, lifetime.Token);
                backoff = TimeSpan.FromMilliseconds(Math.Min(plan.Limits.ReconnectMaximum.TotalMilliseconds,
                    Math.Max(plan.Limits.ReconnectMinimum.TotalMilliseconds, backoff.TotalMilliseconds * 2)));
            }
            await coordinator.UpdateAsync(session, epoch, OmsPrivateStreamPhase.Stopped,
                OmsPrivateStreamProblem.AwaitingReconciliation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || session.Cancellation.IsCancellationRequested) { }
        finally
        {
            if (cancellationToken.IsCancellationRequested || session.Cancellation.IsCancellationRequested)
                await coordinator.UpdateAsync(session, epoch, OmsPrivateStreamPhase.Stopped,
                    OmsPrivateStreamProblem.AwaitingReconciliation);
            ExitRun();
        }
    }

    private sealed class CaptureEpoch(IDeribitPrivateSocket socket, DeribitOptions options, Uri endpoint,
        DeribitPrivateCapturePlan plan, OmsPrivateCaptureCoordinator coordinator,
        OmsPrivateCaptureSession session, Guid epoch, TimeProvider clock, DeribitPrivateCaptureDiagnostics? diagnostics)
    {
        private readonly Channel<OmsPrivateEventEnvelope> queue = Channel.CreateBounded<OmsPrivateEventEnvelope>(
            new BoundedChannelOptions(plan.Limits.BufferCapacity)
            { SingleWriter = true, SingleReader = true, AllowSynchronousContinuations = false, FullMode = BoundedChannelFullMode.Wait });
        private readonly TaskCompletionSource verified = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task consumer = Task.CompletedTask;
        private long nextId;
        private long? pendingTest;
        private long testStarted;

        public async Task RunAsync(CancellationToken root)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(root);
            using var renewal = new CancellationTokenSource();
            using var epochLifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, renewal.Token);
            var ct = epochLifetime.Token;
            consumer = ConsumeAsync(ct);
            try
            {
                using (var connect = Deadline(ct, plan.Limits.RpcTimeout))
                    await socket.ConnectAsync(endpoint, connect.Token);
                await coordinator.UpdateAsync(session, epoch, OmsPrivateStreamPhase.Authenticating,
                    OmsPrivateStreamProblem.AwaitingReconciliation, ct);
                var auth = await RequestAsync("public/auth", new
                {
                    grant_type = "client_credentials", client_id = options.ClientId, client_secret = options.ClientSecret,
                    scope = "connection trade:read account:read wallet:none block_trade:none block_rfq:none"
                }, ct);
                var expires = ValidateAuth(auth);
                diagnostics?.Authenticated();
                // Connection-scoped tokens are renewed by closing/re-authenticating, never refreshing on another socket.
                renewal.CancelAfter(TimeSpan.FromSeconds(Math.Min(plan.Limits.MaxConnectionLifetime.TotalSeconds, expires * 0.8)));
                var currency = plan.Instruments.Values.OrderBy(x => x.Currency, StringComparer.Ordinal).First().Currency;
                var account = await RequestAsync("private/get_account_summary", new { currency, extended = true }, ct);
                if (!account.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number) || number != plan.ExpectedAccountId ||
                    ReadString(account, "type") != (plan.IsSubaccount ? "subaccount" : "main") || ReadString(account, "currency") != currency)
                    throw new DeribitCaptureException(OmsPrivateStreamProblem.AccountMismatch, false);
                diagnostics?.AccountVerified();
                var heartbeat = await RequestAsync("public/set_heartbeat", new { interval = plan.Limits.HeartbeatSeconds }, ct);
                if (heartbeat.ValueKind != JsonValueKind.String || heartbeat.GetString() != "ok")
                    throw new DeribitCaptureException(OmsPrivateStreamProblem.ProtocolViolation);
                await coordinator.UpdateAsync(session, epoch, OmsPrivateStreamPhase.Subscribing,
                    OmsPrivateStreamProblem.AwaitingReconciliation, ct);
                var subscribed = await RequestAsync("private/subscribe", new { channels = plan.Channels }, ct);
                if (subscribed.ValueKind != JsonValueKind.Array || subscribed.GetArrayLength() != plan.Channels.Length ||
                    !subscribed.EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal).SetEquals(plan.Channels))
                    throw new DeribitCaptureException(OmsPrivateStreamProblem.ProtocolViolation);
                await coordinator.UpdateAsync(session, epoch, OmsPrivateStreamPhase.Recovering,
                    OmsPrivateStreamProblem.AwaitingReconciliation, ct);
                diagnostics?.Subscribed();
                verified.TrySetResult();
                while (true) await ReceiveAndRouteAsync(null, ct);
            }
            catch (DeribitCaptureException) { throw; }
            catch (OperationCanceledException) when (!root.IsCancellationRequested && renewal.IsCancellationRequested)
            { throw new DeribitCaptureException(OmsPrivateStreamProblem.Renewal); }
            catch (OperationCanceledException) when (!root.IsCancellationRequested)
            { throw new DeribitCaptureException(OmsPrivateStreamProblem.TransportLost); }
            catch (Exception ex) when (ex is WebSocketException or IOException)
            { throw new DeribitCaptureException(OmsPrivateStreamProblem.TransportLost); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException)
            { throw new DeribitCaptureException(OmsPrivateStreamProblem.ProtocolViolation); }
            finally
            {
                stop.Cancel();
                socket.Abort();
                verified.TrySetCanceled(ct);
                queue.Writer.TryComplete();
                try { await consumer; } catch (Exception) { /* already surfaced or superseded by a recovery outcome */ }
            }
        }

        private async Task ConsumeAsync(CancellationToken ct)
        {
            await verified.Task.WaitAsync(ct);
            await foreach (var envelope in queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    using var deadline = Deadline(ct, plan.Limits.CaptureTimeout);
                    var result = await coordinator.TryCaptureAsync(session, epoch, envelope, deadline.Token);
                    if (!result.HasValue) throw new OperationCanceledException(ct);
                    diagnostics?.CaptureReturned();
                    if (result == OmsPrivateCaptureResult.Collision)
                        await coordinator.UpdateAsync(session, epoch, OmsPrivateStreamPhase.Recovering,
                            OmsPrivateStreamProblem.Collision, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { throw new DeribitCaptureException(OmsPrivateStreamProblem.CaptureFailed, false); }
            }
        }

        private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
        {
            using var deadline = Deadline(ct, plan.Limits.RpcTimeout);
            var id = await SendAsync(method, parameters, deadline.Token);
            while (true)
            {
                var response = await ReceiveAndRouteAsync(id, deadline.Token, method);
                if (response.HasValue) return response.Value;
            }
        }

        private async Task<long> SendAsync(string method, object parameters, CancellationToken ct)
        {
            if (method is not ("public/auth" or "private/get_account_summary" or "public/set_heartbeat" or "private/subscribe" or "public/test"))
                throw new DeribitCaptureException(OmsPrivateStreamProblem.ProtocolViolation, false);
            var id = ++nextId;
            using var deadline = Deadline(ct, plan.Limits.RpcTimeout);
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method, @params = parameters }), deadline.Token);
            return id;
        }

        private async Task<JsonElement?> ReceiveAndRouteAsync(long? expectedId, CancellationToken ct, string? expectedMethod = null)
        {
            var wait = plan.Limits.ReceiveTimeout;
            if (pendingTest.HasValue)
            {
                var remaining = plan.Limits.RpcTimeout - clock.GetElapsedTime(testStarted);
                if (remaining <= TimeSpan.Zero) throw new DeribitCaptureException(OmsPrivateStreamProblem.ProtocolViolation);
                if (remaining < wait) wait = remaining;
            }
            using var deadline = Deadline(ct, wait);
            var receive = socket.ReceiveAsync(plan.Limits.MaxMessageBytes, deadline.Token);
            if (await Task.WhenAny(receive, consumer) != receive)
            {
                deadline.Cancel();
                try { await receive; } catch (Exception) { }
                await consumer; // throws a sanitized capture failure
                throw new DeribitCaptureException(OmsPrivateStreamProblem.CaptureFailed, false);
            }
            var text = await receive;
            if (Encoding.UTF8.GetByteCount(text) > plan.Limits.MaxMessageBytes)
                throw new DeribitCaptureException(OmsPrivateStreamProblem.ProtocolViolation);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (!root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
                throw new DeribitCaptureException(OmsPrivateStreamProblem.ProtocolViolation);
            if (root.TryGetProperty("id", out var responseId))
            {
                if (responseId.ValueKind != JsonValueKind.Number || !responseId.TryGetInt64(out var id))
                    throw new DeribitCaptureException(OmsPrivateStreamProblem.ProtocolViolation);
                if (root.TryGetProperty("error", out _))
                    throw new DeribitCaptureException(expectedMethod == "public/auth" && id == expectedId
                        ? OmsPrivateStreamProblem.AuthenticationRejected : OmsPrivateStreamProblem.ProtocolViolation,
                        expectedMethod != "public/auth");
                if (!root.TryGetProperty("result", out var result)) throw new DeribitCaptureException(OmsPrivateStreamProblem.ProtocolViolation);
                if (id == expectedId) return result.Clone();
                if (id == pendingTest) { pendingTest = null; diagnostics?.Reply(); return null; }
                throw new DeribitCaptureException(OmsPrivateStreamProblem.ProtocolViolation);
            }
            switch (ReadString(root, "method"))
            {
                case "heartbeat":
                    var type = ReadString(root.GetProperty("params"), "type");
                    if (type == "test_request")
                    {
                        if (pendingTest.HasValue) throw new DeribitCaptureException(OmsPrivateStreamProblem.ProtocolViolation);
                        diagnostics?.Challenge();
                        pendingTest = await SendAsync("public/test", new { }, ct);
                        testStarted = clock.GetTimestamp();
                    }
                    else if (type != "heartbeat") throw new DeribitCaptureException(OmsPrivateStreamProblem.ProtocolViolation);
                    return null;
                case "subscription":
                    foreach (var envelope in DeribitWebSocketMessageParser.ParseCaptureEvents(root, plan, session.Scope, epoch, clock.GetUtcNow()))
                    {
                        diagnostics?.Event();
                        if (!queue.Writer.TryWrite(envelope)) throw new DeribitCaptureException(OmsPrivateStreamProblem.BufferOverflow);
                    }
                    return null;
                default: throw new DeribitCaptureException(OmsPrivateStreamProblem.ProtocolViolation);
            }
        }

        private static double ValidateAuth(JsonElement auth)
        {
            if (!auth.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(access.GetString()) || ReadString(auth, "token_type") != "bearer" ||
                !auth.TryGetProperty("expires_in", out var expiry) || expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetDouble(out var seconds) ||
                !double.IsFinite(seconds) || seconds < 10 || seconds > 31536000)
                throw new DeribitCaptureException(OmsPrivateStreamProblem.AuthenticationRejected, false);
            var tokens = ReadString(auth, "scope").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var required in new[] { "connection", "trade:read", "account:read", "wallet:none", "block_trade:none", "block_rfq:none" })
                if (tokens.Count(x => x == required) != 1) throw new DeribitCaptureException(OmsPrivateStreamProblem.AuthenticationRejected, false);
            if (tokens.Any(x => x is not ("connection" or "mainaccount" or "subaccount" or "trade:read" or "account:read" or
                "wallet:none" or "block_trade:none" or "block_rfq:none")))
                throw new DeribitCaptureException(OmsPrivateStreamProblem.AuthenticationRejected, false);
            return seconds;
        }

        private CancellationTokenSource Deadline(CancellationToken parent, TimeSpan duration)
        {
            var result = CancellationTokenSource.CreateLinkedTokenSource(parent);
            result.CancelAfter(duration);
            return result;
        }
    }
}
