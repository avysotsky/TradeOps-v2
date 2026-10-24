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

    public StreamLease Subscribe(string instrument)
    {
        if (!_feeds.TryGetValue(instrument, out var feed))
        {
            if (_feeds.Count >= 4) throw new InvalidOperationException("Instrument stream limit reached.");
            feed = _feeds.GetOrAdd(instrument,
                name => new InstrumentFeed(name, configuration, Release));
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
        private readonly IConfiguration _config;
        private readonly Action<string, InstrumentFeed> _release;
        private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly DeribitRawBook _book;
        private Task? _run;
        private string? _current;
        private long _messages;
        private bool _disposed;

        public InstrumentFeed(string instrument, IConfiguration cfg,
            Action<string, InstrumentFeed> release)
        {
            _name = instrument;
            _config = cfg;
            _release = release;
            _book = new DeribitRawBook(instrument);
        }

        public StreamLease Subscribe()
        {
            if (_disposed) throw new InvalidOperationException("Stream stopped.");
            var id = Guid.NewGuid();
            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true,
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
                    _release(_name, this);
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

        private void State(string state) =>
            Publish(new { type = "status", instrument = _name,
                state, receivedAtUtc = DateTimeOffset.UtcNow });

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

                        using var auth = await ReceiveDocumentAsync(ws, _stop.Token);
                        if (auth.RootElement.TryGetProperty("error", out var authError) &&
                            authError.ValueKind != JsonValueKind.Null)
                            throw new InvalidOperationException("Deribit feed authentication failed.");
                        if (!auth.RootElement.TryGetProperty("id", out var responseId) ||
                            responseId.GetInt32() != 1 ||
                            !auth.RootElement.TryGetProperty("result", out var authResult) ||
                            !authResult.TryGetProperty("access_token", out _))
                            throw new InvalidOperationException("Deribit feed auth not acknowledged.");

                        await SendAsync(ws, new
                        {
                            jsonrpc = "2.0", id = 2,
                            method = "public/subscribe",
                            @params = new {
                                channels = new[] {
                                    "book." + _name + ".raw",
                                    "trades." + _name + ".raw",
                                    "ticker." + _name + ".raw"
                                }
                            }
                        }, _stop.Token);

                        State("waiting_snapshot");
                        backoff = 500;
                        while (ws.State == WebSocketState.Open && !_stop.IsCancellationRequested)
                        {
                            using var frame = await ReceiveDocumentAsync(ws, _stop.Token);
                            var root = frame.RootElement;
                            if (root.TryGetProperty("error", out var err) &&
                                err.ValueKind != JsonValueKind.Null)
                                throw new InvalidOperationException("Deribit subscription error.");
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
                            if (name == "book." + _name + ".raw")
                            {
                                if (!_book.Apply(data, out var view))
                                {
                                    State(view.State);
                                    throw new InvalidOperationException("Deribit raw order book sequence invalid; resubscribe.");
                                }
                                _messages++;
                                Publish(new { type = "book", instrument = _name,
                                    state = "synced", book = view, messages = _messages,
                                    exchangeAgeMs = view.ExchangeTimestamp is long tick
                                      ? Math.Max(0L, view.LocalReceivedAtUnixMs - tick) : (long?)null });
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
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                    catch (Exception)
                    {
                        // Intentionally suppress exception details: auth exceptions
                        // might contain exchange tokens or private key data.
                        _book.Invalidate();
                        State("resyncing");
                    }
                    if (!_stop.IsCancellationRequested)
                    {
                        await Task.Delay(backoff + Random.Shared.Next(0, 200), _stop.Token);
                        backoff = Math.Min(backoff * 2, 10000);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally
            {
                _book.Invalidate();
                State("disconnected");
                foreach (var subscriber in _subscribers.Values)
                    subscriber.Writer.TryComplete();
            }
        }

        private static async Task SendAsync(ClientWebSocket socket, object message,
            CancellationToken ct)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }

        private static async Task<JsonDocument> ReceiveDocumentAsync(
            ClientWebSocket socket, CancellationToken ct)
        {
            // Strictly bounded input. Oversized frames trigger resubscription.
            var buffer = new byte[64 * 1024];
            using var stream = new MemoryStream();
            while (true)
            {
                var chunk = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (chunk.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("Exchange feed closed.");
                if (chunk.MessageType != WebSocketMessageType.Text)
                    throw new InvalidOperationException("Unexpected exchange frame type.");
                if (stream.Length + chunk.Count > 4 * 1024 * 1024)
                    throw new InvalidOperationException("Oversized market-data frame.");
                stream.Write(buffer, 0, chunk.Count);
                if (chunk.EndOfMessage) break;
            }
            return JsonDocument.Parse(stream.GetBuffer().AsMemory(0, (int)stream.Length),
                new JsonDocumentOptions { MaxDepth = 48 });
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
