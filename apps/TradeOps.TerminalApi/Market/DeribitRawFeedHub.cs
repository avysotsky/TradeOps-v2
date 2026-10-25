using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TradeOps.TerminalApi.Market;

/// <summary>
/// One Deribit raw upstream per viewed instrument, shared by local browser
/// subscribers. L2 book reconstruction runs on the receive loop and is NOT
/// throttled by UI frame rates. Per-subscriber bounded latest-state queues
/// prevent slow React clients from blocking market-data ingestion.
/// No trading methods, worker or DB dependency.
/// </summary>
public sealed class DeribitRawFeedHub(IConfiguration configuration) : IDisposable
{
    private readonly ConcurrentDictionary<string, InstrumentFeed> _feeds = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public StreamLease Subscribe(string instrument, int depth)
    {
        if (!DeribitBookSubscription.ValidDepth(depth))
            throw new ArgumentOutOfRangeException(nameof(depth));
        var key = instrument + "|" + depth;
        if (!_feeds.TryGetValue(key, out var feed))
        {
            if (_feeds.Count >= 4) throw new InvalidOperationException("Instrument stream limit reached.");
            feed = _feeds.GetOrAdd(key,
                _ => new InstrumentFeed(instrument, depth, key, configuration, Release));
        }
        return feed.Subscribe();
    }

    private void Release(string instrument, InstrumentFeed feed) =>
        ((ICollection<KeyValuePair<string, InstrumentFeed>>)_feeds)
            .Remove(new KeyValuePair<string, InstrumentFeed>(instrument, feed));

    public void Dispose()
    {
        foreach (var f in _feeds.Values) f.Dispose();
        _feeds.Clear();
    }

    public sealed class StreamLease(ChannelReader<string> reader, Action dispose) : IDisposable
    {
        public ChannelReader<string> Reader { get; } = reader;
        public void Dispose() => dispose();
    }

    private sealed class InstrumentFeed : IDisposable
    {
        private readonly string _name;
        private readonly int _depth;
        private readonly string _key;
        private readonly string _bookChannel;
        private readonly IConfiguration _config;
        private readonly Action<string, InstrumentFeed> _release;
        private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly DeribitRawBook _book;
        private readonly DeribitOwnOrderBook _ownOrders;
        private Task? _run;
        private string? _current;
        private long _messages;
        private bool _disposed;
        private string _publicL2State = "disconnected";
        private string _privateState = "loading";
        private string? _lastFailureCode;
        private DeribitRawBook.BookFailure? _lastBookFailure;
        private readonly Dictionary<string, long> _failureCounts = new(StringComparer.Ordinal);
        private long _reconnectAttempts;
        private DateTimeOffset? _lastFailureUtc;

        // Only sanitized failure categories and public book change IDs can
        // cross the localhost diagnostics boundary. Never serialize exceptions.
        private sealed class FeedFaultException(string code) : Exception
        {
            public string Code { get; } = code;
        }

        private void Count(string code)
        {
            _failureCounts.TryGetValue(code, out var value);
            _failureCounts[code] = value + 1;
        }

        private void Failure(string code)
        {
            _lastFailureCode = code;
            _lastFailureUtc = DateTimeOffset.UtcNow;
            if (code.StartsWith("L2_", StringComparison.Ordinal))
                _lastBookFailure = _book.LastFailure;
            Count(code);
        }

        public InstrumentFeed(string instrument, int depth, string key, IConfiguration cfg,
            Action<string, InstrumentFeed> release)
        {
            _name = instrument;
            _depth = depth;
            _key = key;
            _bookChannel = DeribitBookSubscription.Channel(instrument, depth);
            _config = cfg;
            _release = release;
            _book = new DeribitRawBook(instrument);
            _ownOrders = new DeribitOwnOrderBook(instrument);
        }

        public StreamLease Subscribe()
        {
            if (_disposed) throw new InvalidOperationException("Stream stopped.");
            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            });
            if (!_subscribers.TryAdd(id, channel)) throw new InvalidOperationException();
            if (_current is not null) channel.Writer.TryWrite(_current);
            _run ??= Task.Run(RunAsync);
            return new StreamLease(channel.Reader, () =>
            {
                if (_subscribers.TryRemove(id, out var removed))
                    removed.Writer.TryComplete();
                if (_subscribers.IsEmpty)
                {
                    _release(_key, this);
                    Dispose();
                }
            });
        }

        private void Publish<T>(T message)
        {
            var serialized = JsonSerializer.Serialize(message, Json);
            _current = serialized;
            foreach (var channel in _subscribers.Values)
                channel.Writer.TryWrite(serialized);
        }

        private void State(string state)
        {
            _publicL2State = state;
            Publish(new { type = "status", instrument = _name,
                state, receivedAtUtc = DateTimeOffset.UtcNow,
                depth = _depth, bookChannel = _bookChannel,
                diagnostics = new {
                    requestedDepth = _depth,
                    exchangeChannel = _bookChannel,
                    exchangeMode = DeribitBookSubscription.Mode(_depth),
                    publicL2State = _publicL2State,
                    privateOrdersState = _privateState,
                    reasonCode = _lastFailureCode,
                    failureAtUtc = _lastFailureUtc,
                    expectedChangeId = _lastBookFailure?.ExpectedChangeId,
                    prevChangeId = _lastBookFailure?.PrevChangeId,
                    changeId = _lastBookFailure?.ChangeId,
                    reconnectAttempts = _reconnectAttempts,
                    counters = _failureCounts.ToDictionary(x => x.Key, x => x.Value)
                } });
        }

        private async Task RunAsync()
        {
            var backoff = 500;
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                    try
                    {
                        _book.Invalidate();
                        _ownOrders.Reset("loading");
                        _privateState = "loading";
                        State("connecting");
                        var host = (_config["DeribitTerminal:Environment"] ?? "testnet") switch
                        {
                            "mainnet" => "www.deribit.com",
                            "testnet" => "test.deribit.com",
                            _ => throw new InvalidOperationException("Unsupported exchange environment.")
                        };
                        await ws.ConnectAsync(new Uri("wss://" + host + "/ws/api/v2"),
                            _stop.Token);

                        // Raw channels require an authorized connection. Existing
                        // DPAPI read-only credentials are already in the .NET process.
                        var id = _config["DeribitTerminal:ClientId"];
                        var secret = _config["DeribitTerminal:ClientSecret"];
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
                            throw new InvalidOperationException("Read-only credentials missing.");
                        await SendAsync(ws, new
                        {
                            jsonrpc = "2.0", id = 1,
                            method = "public/auth",
                            @params = new {
                                grant_type = "client_credentials",
                                client_id = id, client_secret = secret
                            }
                        }, _stop.Token);

                        using var authDeadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        authDeadline.CancelAfter(TimeSpan.FromSeconds(40));
                        using var auth = await ReceiveDocumentAsync(ws, authDeadline.Token);
                        if (auth.RootElement.TryGetProperty("error", out var authError) &&
                            authError.ValueKind != JsonValueKind.Null)
                            throw new FeedFaultException("WS_AUTH_SUBSCRIPTION");
                        if (!auth.RootElement.TryGetProperty("id", out var responseId) ||
                            responseId.GetInt32() != 1 ||
                            !auth.RootElement.TryGetProperty("result", out var authResult) ||
                            !authResult.TryGetProperty("access_token", out _))
                            throw new FeedFaultException("WS_AUTH_SUBSCRIPTION");

                        await SendAsync(ws, new
                        {
                            jsonrpc = "2.0", id = 2,
                            method = "public/subscribe",
                            @params = new {
                                channels = new[] {
                                    _bookChannel,
                                    "trades." + _name + ".raw",
                                    "ticker." + _name + ".raw"
                                }
                            }
                        }, _stop.Token);

                        // Application-level liveness independent of rare book
                        // changes; Deribit sends heartbeat/test_request and
                        // closes a client that doesn't respond to public/test.
                        await SendAsync(ws, new {
                            jsonrpc = "2.0", id = 4,
                            method = "public/set_heartbeat",
                            @params = new { interval = 15 }
                        }, _stop.Token);

                        // A separate PRIVATE read subscription, NEVER a trading
                        // request. If missing trade:read, public RAW depth must
                        // remain available and private overlay stays unavailable.
                        await SendAsync(ws, new {
                            jsonrpc = "2.0", id = 5,
                            method = "private/subscribe",
                            @params = new {
                                channels = new[] { "user.orders." + _name + ".raw" }
                            }
                        }, _stop.Token);

                        State("waiting_snapshot");
                        backoff = 500;
                        while (ws.State == WebSocketState.Open && !_stop.IsCancellationRequested)
                        {
                            using var receiveDeadline =
                                CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                            receiveDeadline.CancelAfter(TimeSpan.FromSeconds(40));
                            using var frame = await ReceiveDocumentAsync(ws, receiveDeadline.Token);
                            var root = frame.RootElement;
                            // Private feed/initial snapshot errors must never
                            // disconnect or delay the PUBLIC market L2 stream.
                            if (root.TryGetProperty("id", out var rpcId) &&
                                rpcId.ValueKind == JsonValueKind.Number &&
                                rpcId.TryGetInt32(out var callId))
                            {
                                if (callId == 5)
                                {
                                    if (root.TryGetProperty("error", out var subscriptionError) &&
                                        subscriptionError.ValueKind != JsonValueKind.Null)
                                    {
                                        _ownOrders.Reset("unavailable");
                                        _privateState = "unavailable";
                                        Count("PRIVATE_SUBSCRIPTION_UNAVAILABLE");
                                        Publish(new { type = "own_orders", instrument = _name,
                                            orders = _ownOrders.View() });
                                    }
                                    else if (root.TryGetProperty("result", out var subscriptions) &&
                                             subscriptions.ValueKind == JsonValueKind.Array &&
                                             subscriptions.EnumerateArray().Any(x =>
                                                 x.ValueKind == JsonValueKind.String &&
                                                 x.GetString() == "user.orders." + _name + ".raw"))
                                    {
                                        // Snapshot request AFTER subscription ack.
                                        // Stream updates received before response
                                        // are buffered by DeribitOwnOrderBook.
                                        await SendAsync(ws, new { jsonrpc = "2.0", id = 6,
                                            method = "private/get_open_orders_by_instrument",
                                            @params = new { instrument_name = _name }
                                        }, _stop.Token);
                                    }
                                    else
                                    {
                                        _ownOrders.Reset("unavailable");
                                        _privateState = "unavailable";
                                        Count("PRIVATE_SUBSCRIPTION_UNAVAILABLE");
                                        Publish(new { type = "own_orders", instrument = _name,
                                            orders = _ownOrders.View() });
                                    }
                                    continue;
                                }
                                if (callId == 6)
                                {
                                    try
                                    {
                                        if ((root.TryGetProperty("error", out var snapshotError) &&
                                             snapshotError.ValueKind != JsonValueKind.Null) ||
                                            !root.TryGetProperty("result", out var snapshotOrders) ||
                                            !_ownOrders.Hydrate(snapshotOrders))
                                        {
                                            _ownOrders.Reset("unavailable");
                                            _privateState = "unavailable";
                                            Count("PRIVATE_SNAPSHOT_UNAVAILABLE");
                                        }
                                        else _privateState = "ready";
                                    }
                                    catch (Exception)
                                    {
                                        // Bad PRIVATE snapshot must not reset PUBLIC L2.
                                        _ownOrders.Reset("unavailable");
                                        _privateState = "unavailable";
                                        Count("PRIVATE_SNAPSHOT_UNAVAILABLE");
                                    }
                                    Publish(new { type = "own_orders", instrument = _name,
                                        orders = _ownOrders.View() });
                                    continue;
                                }
                            }
                            if (root.TryGetProperty("error", out var err) &&
                                err.ValueKind != JsonValueKind.Null)
                                throw new FeedFaultException("WS_AUTH_SUBSCRIPTION");
                            if (!root.TryGetProperty("method", out var method) ||
                                method.ValueKind != JsonValueKind.String)
                                continue;
                            if (method.GetString() == "heartbeat")
                            {
                                if (root.TryGetProperty("params", out var heartbeat) &&
                                    heartbeat.TryGetProperty("type", out var htype) &&
                                    htype.GetString() == "test_request")
                                    await SendAsync(ws, new { jsonrpc = "2.0", id = 3,
                                        method = "public/test" }, _stop.Token);
                                continue;
                            }
                            if (method.GetString() != "subscription" ||
                                !root.TryGetProperty("params", out var parameters) ||
                                !parameters.TryGetProperty("channel", out var chan) ||
                                !parameters.TryGetProperty("data", out var data))
                                continue;
                            var name = chan.GetString();
                            if (name == _bookChannel)
                            {
                                DeribitRawBook.BookView view;
                                var applied = DeribitBookSubscription.IsAggregated(_depth)
                                    ? _book.ApplyGroupedSnapshot(data, _depth, out view)
                                    : _book.Apply(data, out view);
                                if (!applied)
                                {
                                    throw new FeedFaultException(
                                        _book.LastFailure?.Code ?? "L2_MALFORMED");
                                }
                                _messages++;
                                if (_publicL2State != "synced") State("synced");
                                Publish(new { type = "book", instrument = _name,
                                    depth = _depth, bookChannel = _bookChannel,
                                    state = "synced", book = view, ownOrders = _ownOrders.View(),
                                    messages = _messages,
                                    exchangeAgeMs = view.ExchangeTimestamp is long tick
                                      ? Math.Max(0L, view.LocalReceivedAtUnixMs - tick) : (long?)null });
                            }
                            else if (name == "user.orders." + _name + ".raw" &&
                                     data.ValueKind == JsonValueKind.Object)
                            {
                                // Never publish raw order IDs/labels/account data
                                // to React. Aggregate remaining resting volume
                                // by instrument + side + exact price, and only
                                // after initial snapshot reconciliation.
                                try
                                {
                                    if (!_ownOrders.Notification(data))
                                    {
                                        _ownOrders.Reset("unavailable");
                                        _privateState = "unavailable";
                                        Count("PRIVATE_NOTIFICATION_INVALID");
                                    }
                                }
                                catch (Exception)
                                {
                                    _ownOrders.Reset("unavailable");
                                    _privateState = "unavailable";
                                    Count("PRIVATE_NOTIFICATION_INVALID");
                                }
                                Publish(new { type = "own_orders", instrument = _name,
                                    orders = _ownOrders.View() });
                            }
                            else if (name == "trades." + _name + ".raw" &&
                                     data.ValueKind == JsonValueKind.Array)
                            {
                                // Limit serialized trade payload to avoid browser floods.
                                var items = data.EnumerateArray().Take(30)
                                    .Select(x => new {
                                        tradeId = String(x, "trade_id"),
                                        direction = String(x, "direction"),
                                        price = Decimal(x, "price"),
                                        amount = Decimal(x, "amount"),
                                        timestamp = Long(x, "timestamp")
                                    }).ToArray();
                                Publish(new { type = "trades", instrument = _name,
                                    state = "synced", trades = items,
                                    receivedAtUtc = DateTimeOffset.UtcNow });
                            }
                            else if (name == "ticker." + _name + ".raw" &&
                                     data.ValueKind == JsonValueKind.Object)
                            {
                                Publish(new { type = "ticker", instrument = _name,
                                    bestBidPrice = Decimal(data, "best_bid_price"),
                                    bestAskPrice = Decimal(data, "best_ask_price"),
                                    markPrice = Decimal(data, "mark_price"),
                                    lastPrice = Decimal(data, "last_price"),
                                    indexPrice = Decimal(data, "index_price"),
                                    timestampUtc = DateTimeOffset.UtcNow });
                            }
                        }
                        // Clean state transitions are not a substitute for an
                        // observed close reason; report a transport loss.
                        if (!_stop.IsCancellationRequested)
                            throw new FeedFaultException("WS_CLOSED");
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        // Sanitize error types; never log ToString(), Message,
                        // private payloads, account IDs or credentials.
                        var code = ex switch
                        {
                            FeedFaultException fault => fault.Code,
                            OperationCanceledException => "WS_TIMEOUT",
                            WebSocketException => "WS_CLOSED",
                            JsonException => "WS_MALFORMED_FRAME",
                            _ => "WS_UNEXPECTED"
                        };
                        Failure(code);
                        _book.Invalidate();
                        _ownOrders.Reset("resyncing");
                        _privateState = "resyncing";
                        State("resyncing");
                    }
                    if (!_stop.IsCancellationRequested)
                    {
                        _reconnectAttempts++;
                        Count("RECONNECT_ATTEMPT");
                        await Task.Delay(backoff + Random.Shared.Next(0, 200), _stop.Token);
                        backoff = Math.Min(backoff * 2, 10000);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally
            {
                _book.Invalidate();
                _ownOrders.Reset("disconnected");
                _privateState = "disconnected";
                State("disconnected");
                foreach (var subscriber in _subscribers.Values)
                    subscriber.Writer.TryComplete();
            }
        }

        private static async Task SendAsync(ClientWebSocket socket, object message,
            CancellationToken ct)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct);
        }

        private static async Task<JsonDocument> ReceiveDocumentAsync(
            ClientWebSocket socket, CancellationToken ct)
        {
            // Strictly bounded input. Oversized frames trigger resubscription.
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                using var stream = new MemoryStream(16 * 1024);
                while (true)
                {
                    var chunk = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                    if (chunk.MessageType == WebSocketMessageType.Close)
                        throw new FeedFaultException("WS_CLOSED");
                    if (chunk.MessageType != WebSocketMessageType.Text)
                        throw new FeedFaultException("WS_MALFORMED_FRAME");
                    if (stream.Length + chunk.Count > 4 * 1024 * 1024)
                        throw new FeedFaultException("WS_MALFORMED_FRAME");
                    stream.Write(buffer, 0, chunk.Count);
                    if (chunk.EndOfMessage) break;
                }
                return JsonDocument.Parse(stream.GetBuffer().AsMemory(0, (int)stream.Length),
                    new JsonDocumentOptions { MaxDepth = 48 });
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        private static string? String(JsonElement e, string key) =>
            e.ValueKind == JsonValueKind.Object &&
            e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;
        private static decimal? Decimal(JsonElement e, string key) =>
            e.ValueKind == JsonValueKind.Object &&
            e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number &&
            p.TryGetDecimal(out var value) ? value : null;
        private static long? Long(JsonElement e, string key) =>
            e.ValueKind == JsonValueKind.Object &&
            e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number &&
            p.TryGetInt64(out var value) ? value : null;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            // Let the running receive loop own cleanup and cancellation.
        }
    }
}
