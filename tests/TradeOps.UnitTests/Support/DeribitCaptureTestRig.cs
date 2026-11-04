using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Oms;
using TradeOps.Domain.Oms;
using TradeOps.Infrastructure.Exchange.Deribit;

namespace TradeOps.UnitTests.Support;

internal static class DeribitCaptureTestRig
{
    internal const string ReadScope = "connection mainaccount trade:read account:read wallet:none block_trade:none block_rfq:none";
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);
    internal static AccountKey Scope(string account = "account", string? sub = null) => new(new("tenant"), new("Deribit"),
        OmsEnvironment.Testnet, new(account), sub is null ? null : new TradingSubaccountId(sub));
    internal static DeribitPrivateCapturePlan Plan(long account = 123, DeribitPrivateStreamLimits? limits = null, bool sub = false) =>
        new(account, sub, new[] { new DeribitPrivateInstrument("BTC-PERPETUAL", "future", "BTC", OmsPrivateAmountUnit.Usd, "USD"),
            new DeribitPrivateInstrument("ETH_USDC-25DEC26", "future", "USDC", OmsPrivateAmountUnit.BaseCoin, "ETH"),
            new DeribitPrivateInstrument("BTC-25DEC26-60000-C", "option", "BTC", OmsPrivateAmountUnit.BaseCoin, "BTC") },
            limits ?? new() { ReconnectMinimum = TimeSpan.FromMilliseconds(5), ReconnectMaximum = TimeSpan.FromMilliseconds(20) });
    internal static DeribitOptions Options(string url = "wss://test.deribit.com/ws/api/v2") =>
        new() { WebSocketUrl = url, ClientId = "synthetic-client", ClientSecret = "synthetic-secret" };
    internal static DeribitPrivateWebSocketStream Stream(Func<IDeribitPrivateSocket> factory, DeribitOptions? options = null) =>
        new(options ?? Options(), NullLogger<DeribitPrivateWebSocketStream>.Instance, factory);

    internal static string Trade(string instrument = "BTC-PERPETUAL", string currency = "BTC", string kind = "future",
        decimal amount = 10m, decimal? contracts = 1m, string tradeId = "trade-1") => JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", method = "subscription", @params = new
            {
                channel = $"user.trades.{kind}.{currency}.100ms",
                data = new[] { new { instrument_name = instrument, order_id = "order-1", trade_id = tradeId,
                    direction = "buy", amount, contracts, price = 65000m, fee = -0.00001m, fee_currency = currency,
                    timestamp = 1793793600000L } }
            }
        });

    internal sealed class Socket : IDeribitPrivateSocket
    {
        private readonly Channel<Func<string>> frames = Channel.CreateUnbounded<Func<string>>();
        internal readonly ConcurrentQueue<(string Method, JsonElement Parameters)> Sent = new();
        internal readonly TaskCompletionSource AuthSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Tested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action<Socket, long, JsonElement>? OnSubscribe;
        internal Action<Socket, string, long>? OnRequest;
        internal string GrantedScope = ReadScope;
        internal long AccountId = 123;
        internal string AccountType = "main";
        internal bool HoldAuth;
        internal bool IgnoreTest;
        internal bool Aborted;
        internal int Connections;
        internal Uri? Endpoint;
        internal void Emit(string text) => frames.Writer.TryWrite(() => text);
        internal void Fail(Exception ex) => frames.Writer.TryWrite(() => throw ex);
        internal void Reply(long id, object result) => Emit(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }));
        internal void Heartbeat() => Emit("""{"jsonrpc":"2.0","method":"heartbeat","params":{"type":"test_request"}}""");
        public Task ConnectAsync(Uri uri, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Endpoint = uri; Connections++; return Task.CompletedTask; }
        public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            var method = root.GetProperty("method").GetString()!;
            var id = root.GetProperty("id").GetInt64();
            var parameters = root.GetProperty("params").Clone();
            Sent.Enqueue((method, parameters));
            OnRequest?.Invoke(this, method, id);
            switch (method)
            {
                case "public/auth":
                    AuthSent.TrySetResult();
                    if (!HoldAuth) Reply(id, new { access_token = "synthetic-token", token_type = "bearer", expires_in = 900, scope = GrantedScope });
                    break;
                case "private/get_account_summary": Reply(id, new { id = AccountId, type = AccountType, currency = parameters.GetProperty("currency").GetString() }); break;
                case "public/set_heartbeat": Reply(id, "ok"); break;
                case "private/subscribe":
                    if (OnSubscribe is null) Reply(id, parameters.GetProperty("channels"));
                    else OnSubscribe(this, id, parameters);
                    Subscribed.TrySetResult();
                    break;
                case "public/test":
                    if (!IgnoreTest) Reply(id, new { version = "synthetic" });
                    Tested.TrySetResult();
                    break;
                default: throw new InvalidOperationException("Unexpected mutating/non-whitelisted RPC.");
            }
            return Task.CompletedTask;
        }
        public async Task<string> ReceiveAsync(int maximumBytes, CancellationToken ct) => (await frames.Reader.ReadAsync(ct))();
        public void Abort() => Aborted = true;
        public void Dispose() => Aborted = true;
    }

    internal sealed class Capture : IOmsPrivateEventCapture
    {
        private readonly Channel<OmsPrivateEventEnvelope> committed = Channel.CreateUnbounded<OmsPrivateEventEnvelope>();
        internal Func<OmsPrivateEventEnvelope, CancellationToken, Task<OmsPrivateCaptureResult>>? BeforeCommit;
        internal readonly ConcurrentQueue<OmsPrivateEventEnvelope> Events = new();
        public async Task<OmsPrivateCaptureResult> CaptureAsync(OmsPrivateEventEnvelope envelope, CancellationToken ct = default)
        {
            var result = BeforeCommit is null ? OmsPrivateCaptureResult.Inserted : await BeforeCommit(envelope, ct);
            Events.Enqueue(envelope);
            committed.Writer.TryWrite(envelope);
            return result;
        }
        internal Task<OmsPrivateEventEnvelope> Next() => committed.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
    }
}
