using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;

namespace TradeOps.Infrastructure.Exchange.Deribit;

public sealed class DeribitPrivateWebSocketStream(
    DeribitOptions options,
    ILogger<DeribitPrivateWebSocketStream> logger) : IExchangeEventStream
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private const int AuthRequestId = 1;
    private const int SubscribeRequestId = 2;

    public bool IsEnabled => true;

    public async Task RunAsync(
        Func<ExchangeOrderUpdate, CancellationToken, Task> onOrderUpdate,
        Func<ExchangeExecutionUpdate, CancellationToken, Task> onExecutionUpdate,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration(options);

        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(
            Math.Max(10, options.WebSocketPingIntervalSeconds));

        await socket.ConnectAsync(
            new Uri(options.WebSocketUrl),
            cancellationToken);

        await SendJsonAsync(
            socket,
            new
            {
                jsonrpc = "2.0",
                id = AuthRequestId,
                method = "public/auth",
                @params = new
                {
                    grant_type = "client_credentials",
                    client_id = options.ClientId,
                    client_secret = options.ClientSecret
                }
            },
            cancellationToken);

        var authResult = await WaitForResultAsync(
            socket,
            AuthRequestId,
            cancellationToken);

        var accessToken = ReadString(
            authResult,
            "access_token");

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException(
                "Deribit websocket authentication did not return an access token.");
        }

        await SendJsonAsync(
            socket,
            new
            {
                jsonrpc = "2.0",
                id = SubscribeRequestId,
                method = "private/subscribe",
                @params = new
                {
                    access_token = accessToken,
                    channels = new[]
                    {
                        "user.orders.future.any.raw",
                        "user.trades.future.any.100ms"
                    }
                }
            },
            cancellationToken);

        _ = await WaitForResultAsync(
            socket,
            SubscribeRequestId,
            cancellationToken);

        logger.LogInformation(
            "Connected to Deribit testnet private websocket and subscribed to raw order / 100ms trade streams.");

        while (!cancellationToken.IsCancellationRequested)
        {
            var json = await ReceiveTextAsync(
                socket,
                cancellationToken);

            foreach (var update in
                     DeribitWebSocketMessageParser.ParseOrderUpdates(json))
            {
                await onOrderUpdate(
                    update,
                    cancellationToken);
            }

            foreach (var update in
                     DeribitWebSocketMessageParser.ParseExecutionUpdates(json))
            {
                await onExecutionUpdate(
                    update,
                    cancellationToken);
            }
        }
    }

    internal static void ValidateConfiguration(DeribitOptions value)
    {
        if (!Uri.TryCreate(
                value.WebSocketUrl,
                UriKind.Absolute,
                out var uri)
            || !string.Equals(
                uri.Scheme,
                "wss",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                uri.Host,
                "test.deribit.com",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                uri.AbsolutePath.TrimEnd('/'),
                "/ws/api/v2",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Deribit private websocket is restricted to the official testnet endpoint.");
        }

        if (string.IsNullOrWhiteSpace(value.ClientId)
            || string.IsNullOrWhiteSpace(value.ClientSecret))
        {
            throw new InvalidOperationException(
                "Deribit testnet private websocket requires ClientId and ClientSecret configuration.");
        }
    }

    private static async Task<JsonElement> WaitForResultAsync(
        ClientWebSocket socket,
        int requestId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var json = await ReceiveTextAsync(
                socket,
                cancellationToken);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!TryReadInt32(root, "id", out var responseId)
                || responseId != requestId)
            {
                continue;
            }

            if (root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                var code = ReadString(error, "code");
                var message = ReadString(error, "message");

                throw new InvalidOperationException(
                    $"Deribit websocket request {requestId} failed: {code} {message}".Trim());
            }

            if (!root.TryGetProperty("result", out var result))
            {
                throw new InvalidOperationException(
                    $"Deribit websocket request {requestId} did not return result.");
            }

            return result.Clone();
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private static async Task<string> ReceiveTextAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();

        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException(
                    $"Deribit websocket closed: {result.CloseStatus} {result.CloseStatusDescription}");
            }

            if (result.MessageType == WebSocketMessageType.Text)
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
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            payload,
            JsonOptions);

        return socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            cancellationToken);
    }

    private static bool TryReadInt32(
        JsonElement element,
        string name,
        out int value)
    {
        value = 0;

        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(property.GetString(), out value),
            _ => false
        };
    }

    private static string ReadString(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }
}
