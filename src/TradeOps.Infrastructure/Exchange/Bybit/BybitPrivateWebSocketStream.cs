using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;

namespace TradeOps.Infrastructure.Exchange.Bybit;

public sealed class BybitPrivateWebSocketStream(
    BybitOptions options,
    ILogger<BybitPrivateWebSocketStream> logger) : IExchangeEventStream
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsEnabled => true;

    public async Task RunAsync(
        Func<ExchangeOrderUpdate, CancellationToken, Task> onOrderUpdate,
        Func<ExchangeExecutionUpdate, CancellationToken, Task> onExecutionUpdate,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(options.PrivateWebSocketUrl), cancellationToken);

        var expires = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000;
        var signature = CreateAuthSignature(options.ApiSecret, expires);

        await SendJsonAsync(socket, new
        {
            op = "auth",
            args = new object[] { options.ApiKey, expires, signature }
        }, cancellationToken);
        await WaitForSuccessAsync(socket, "auth", cancellationToken);

        await SendJsonAsync(socket, new
        {
            op = "subscribe",
            args = new[] { $"order.{options.Category}", $"execution.{options.Category}" }
        }, cancellationToken);
        await WaitForSuccessAsync(socket, "subscribe", cancellationToken);

        logger.LogInformation("Connected to Bybit private websocket and subscribed to order/execution {Category} streams.", options.Category);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receiveTask = ReceiveLoopAsync(socket, onOrderUpdate, onExecutionUpdate, linkedCts.Token);
        var heartbeatTask = HeartbeatLoopAsync(socket, linkedCts.Token);

        var completed = await Task.WhenAny(receiveTask, heartbeatTask);
        linkedCts.Cancel();

        await completed;

        try
        {
            await Task.WhenAll(receiveTask, heartbeatTask);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
        }
    }

    internal static string CreateAuthSignature(string apiSecret, long expires)
    {
        var payload = $"GET/realtime{expires}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiSecret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        Func<ExchangeOrderUpdate, CancellationToken, Task> onOrderUpdate,
        Func<ExchangeExecutionUpdate, CancellationToken, Task> onExecutionUpdate,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var json = await ReceiveTextAsync(socket, cancellationToken);

            foreach (var update in BybitWebSocketMessageParser.ParseOrderUpdates(json))
            {
                await onOrderUpdate(update, cancellationToken);
            }

            foreach (var update in BybitWebSocketMessageParser.ParseExecutionUpdates(json))
            {
                await onExecutionUpdate(update, cancellationToken);
            }
        }
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(10, options.WebSocketPingIntervalSeconds));
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(delay, cancellationToken);
            await SendJsonAsync(socket, new { op = "ping" }, cancellationToken);
        }
    }

    private static async Task WaitForSuccessAsync(
        ClientWebSocket socket,
        string expectedOperation,
        CancellationToken cancellationToken)
    {
        var json = await ReceiveTextAsync(socket, cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var operation = root.TryGetProperty("op", out var op) ? op.GetString() : null;
        var success = root.TryGetProperty("success", out var successElement)
            && successElement.ValueKind == JsonValueKind.True;

        if (!string.Equals(operation, expectedOperation, StringComparison.OrdinalIgnoreCase) || !success)
        {
            var message = root.TryGetProperty("ret_msg", out var retMsg) ? retMsg.GetString() : "Unknown websocket acknowledgement.";
            throw new InvalidOperationException($"Bybit websocket {expectedOperation} failed: {message}");
        }
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
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException($"Bybit websocket closed: {result.CloseStatus} {result.CloseStatusDescription}");
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Task SendJsonAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private void ValidateConfiguration()
    {
        if (!string.Equals(options.PrivateWebSocketUrl, "wss://stream-testnet.bybit.com/v5/private", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only the official Bybit private testnet websocket endpoint is allowed.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.ApiSecret))
        {
            throw new InvalidOperationException("Bybit testnet websocket requires ApiKey and ApiSecret configuration.");
        }
    }
}
