using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;

namespace TradeOps.Infrastructure.Exchange.Okx;

public sealed class OkxPrivateWebSocketStream : IExchangeEventStream
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly OkxOptions _options;
    private readonly ILogger<OkxPrivateWebSocketStream> _logger;

    public OkxPrivateWebSocketStream(
        OkxOptions options,
        ILogger<OkxPrivateWebSocketStream> logger)
    {
        _options = options;
        _logger = logger;
        ValidateConfiguration(options);
    }

    public bool IsEnabled => true;

    public async Task RunAsync(
        Func<ExchangeOrderUpdate, CancellationToken, Task> onOrderUpdate,
        Func<ExchangeExecutionUpdate, CancellationToken, Task> onExecutionUpdate,
        CancellationToken cancellationToken = default)
    {
        using var socket = new ClientWebSocket();

        await socket.ConnectAsync(
            new Uri(_options.PrivateWebSocketUrl),
            cancellationToken);

        var timestamp =
            DateTimeOffset.UtcNow
                .ToUnixTimeSeconds()
                .ToString(CultureInfo.InvariantCulture);

        await SendJsonAsync(
            socket,
            new
            {
                op = "login",
                args = new[]
                {
                    new
                    {
                        apiKey = _options.ApiKey,
                        passphrase = _options.Passphrase,
                        timestamp,
                        sign = CreateLoginSignature(
                            timestamp,
                            _options.ApiSecret)
                    }
                }
            },
            cancellationToken);

        await WaitForEventAsync(
            socket,
            expectedEvent: "login",
            expectedInstrumentType: null,
            cancellationToken);

        await SubscribeOrdersAsync(
            socket,
            "SWAP",
            cancellationToken);

        await SubscribeOrdersAsync(
            socket,
            "FUTURES",
            cancellationToken);

        _logger.LogInformation(
            "Connected to OKX Demo private websocket and subscribed to SWAP/FUTURES order events.");

        using var linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        var receiveTask = ReceiveLoopAsync(
            socket,
            onOrderUpdate,
            onExecutionUpdate,
            linkedCts.Token);

        var heartbeatTask = HeartbeatLoopAsync(
            socket,
            linkedCts.Token);

        var completed =
            await Task.WhenAny(
                receiveTask,
                heartbeatTask);

        linkedCts.Cancel();

        await completed;

        try
        {
            await Task.WhenAll(
                receiveTask,
                heartbeatTask);
        }
        catch (OperationCanceledException)
            when (linkedCts.IsCancellationRequested)
        {
        }
    }

    internal static string CreateLoginSignature(
        string timestamp,
        string secret) =>
        OkxSigner.Sign(
            timestamp,
            "GET",
            "/users/self/verify",
            string.Empty,
            secret);

    internal static void ValidateConfiguration(
        OkxOptions value)
    {
        if (!Uri.TryCreate(
                value.PrivateWebSocketUrl,
                UriKind.Absolute,
                out var uri)
            || !string.Equals(
                uri.Scheme,
                "wss",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                uri.Host,
                "wspap.okx.com",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                uri.AbsolutePath.TrimEnd('/'),
                "/ws/v5/private",
                StringComparison.Ordinal)
            || uri.Port is not (443 or 8443)
            || !string.IsNullOrEmpty(uri.Query))
        {
            throw new InvalidOperationException(
                "OKX private websocket is restricted to the official OKX Demo private websocket endpoint.");
        }

        if (string.IsNullOrWhiteSpace(value.ApiKey)
            || string.IsNullOrWhiteSpace(value.ApiSecret)
            || string.IsNullOrWhiteSpace(value.Passphrase))
        {
            throw new InvalidOperationException(
                "OKX Demo private websocket requires ApiKey, ApiSecret and Passphrase configuration.");
        }
    }

    private static async Task SubscribeOrdersAsync(
        ClientWebSocket socket,
        string instrumentType,
        CancellationToken cancellationToken)
    {
        await SendJsonAsync(
            socket,
            new
            {
                op = "subscribe",
                args = new[]
                {
                    new
                    {
                        channel = "orders",
                        instType = instrumentType
                    }
                }
            },
            cancellationToken);

        await WaitForEventAsync(
            socket,
            expectedEvent: "subscribe",
            expectedInstrumentType: instrumentType,
            cancellationToken);
    }

    private static async Task WaitForEventAsync(
        ClientWebSocket socket,
        string expectedEvent,
        string? expectedInstrumentType,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var json = await ReceiveTextAsync(
                socket,
                cancellationToken);

            if (string.Equals(
                    json,
                    "pong",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var document =
                JsonDocument.Parse(json);

            var root =
                document.RootElement;

            var eventName =
                ReadString(
                    root,
                    "event");

            if (string.Equals(
                    eventName,
                    "error",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"OKX websocket request failed: {ReadString(root, "code")} {ReadString(root, "msg")}".Trim());
            }

            if (!string.Equals(
                    eventName,
                    expectedEvent,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var code =
                ReadString(
                    root,
                    "code");

            if (!string.IsNullOrWhiteSpace(code)
                && !string.Equals(
                    code,
                    "0",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"OKX websocket {expectedEvent} failed: {code} {ReadString(root, "msg")}".Trim());
            }

            if (expectedInstrumentType is null)
            {
                return;
            }

            if (root.TryGetProperty(
                    "arg",
                    out var argument)
                && argument.ValueKind == JsonValueKind.Object
                && string.Equals(
                    ReadString(
                        argument,
                        "channel"),
                    "orders",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    ReadString(
                        argument,
                        "instType"),
                    expectedInstrumentType,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        throw new OperationCanceledException(
            cancellationToken);
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        Func<ExchangeOrderUpdate, CancellationToken, Task> onOrderUpdate,
        Func<ExchangeExecutionUpdate, CancellationToken, Task> onExecutionUpdate,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var json = await ReceiveTextAsync(
                socket,
                cancellationToken);

            if (string.Equals(
                    json,
                    "pong",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var update in
                     OkxWebSocketMessageParser.ParseOrderUpdates(
                         json))
            {
                await onOrderUpdate(
                    update,
                    cancellationToken);
            }

            foreach (var update in
                     OkxWebSocketMessageParser.ParseExecutionUpdates(
                         json))
            {
                await onExecutionUpdate(
                    update,
                    cancellationToken);
            }
        }
    }

    private async Task HeartbeatLoopAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var delay =
            TimeSpan.FromSeconds(
                Math.Clamp(
                    _options.WebSocketPingIntervalSeconds,
                    5,
                    25));

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(
                delay,
                cancellationToken);

            await SendTextAsync(
                socket,
                "ping",
                cancellationToken);
        }
    }

    private static async Task<string> ReceiveTextAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream =
            new MemoryStream();

        WebSocketReceiveResult result;

        do
        {
            result =
                await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);

            if (result.MessageType ==
                WebSocketMessageType.Close)
            {
                throw new WebSocketException(
                    $"OKX websocket closed: {result.CloseStatus} {result.CloseStatusDescription}");
            }

            if (result.MessageType ==
                WebSocketMessageType.Text)
            {
                stream.Write(
                    buffer,
                    0,
                    result.Count);
            }
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(
            stream.ToArray());
    }

    private static Task SendJsonAsync(
        ClientWebSocket socket,
        object payload,
        CancellationToken cancellationToken)
    {
        var bytes =
            JsonSerializer.SerializeToUtf8Bytes(
                payload,
                JsonOptions);

        return socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            cancellationToken);
    }

    private static Task SendTextAsync(
        ClientWebSocket socket,
        string payload,
        CancellationToken cancellationToken)
    {
        var bytes =
            Encoding.UTF8.GetBytes(
                payload);

        return socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            cancellationToken);
    }

    private static string ReadString(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(
                name,
                out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String =>
                value.GetString() ?? string.Empty,
            JsonValueKind.Number =>
                value.GetRawText(),
            _ => string.Empty
        };
    }
}
