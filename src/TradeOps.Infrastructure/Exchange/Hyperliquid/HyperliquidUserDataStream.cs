using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;

namespace TradeOps.Infrastructure.Exchange.Hyperliquid;

public sealed class HyperliquidUserDataStream : IExchangeEventStream
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HyperliquidOptions _options;
    private readonly ILogger<HyperliquidUserDataStream> _logger;

    public HyperliquidUserDataStream(
        HyperliquidOptions options,
        ILogger<HyperliquidUserDataStream> logger)
    {
        _options = options;
        _logger = logger;
        ValidateConfiguration();
    }

    public bool IsEnabled => true;

    public async Task RunAsync(
        Func<ExchangeOrderUpdate, CancellationToken, Task> onOrderUpdate,
        Func<ExchangeExecutionUpdate, CancellationToken, Task> onExecutionUpdate,
        CancellationToken cancellationToken = default)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        await socket.ConnectAsync(
            new Uri(_options.WebSocketUrl),
            cancellationToken);

        await SendSubscriptionAsync(
            socket,
            "orderUpdates",
            cancellationToken);

        await SendSubscriptionAsync(
            socket,
            "userFills",
            cancellationToken);

        _logger.LogInformation(
            "Connected to Hyperliquid testnet websocket and subscribed to orderUpdates/userFills for {UserAddress}.",
            _options.UserAddress);

        while (!cancellationToken.IsCancellationRequested)
        {
            var json = await ReceiveTextAsync(
                socket,
                cancellationToken);

            foreach (var update in
                     HyperliquidWebSocketMessageParser.ParseOrderUpdates(json))
            {
                await onOrderUpdate(
                    update,
                    cancellationToken);
            }

            foreach (var update in
                     HyperliquidWebSocketMessageParser.ParseExecutionUpdates(json))
            {
                await onExecutionUpdate(
                    update,
                    cancellationToken);
            }
        }
    }

    private Task SendSubscriptionAsync(
        ClientWebSocket socket,
        string type,
        CancellationToken cancellationToken)
    {
        return SendJsonAsync(
            socket,
            new
            {
                method = "subscribe",
                subscription = new
                {
                    type,
                    user = _options.UserAddress
                }
            },
            cancellationToken);
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
                    $"Hyperliquid websocket closed: {result.CloseStatus} {result.CloseStatusDescription}");
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

    private void ValidateConfiguration()
    {
        if (!Uri.TryCreate(
                _options.WebSocketUrl,
                UriKind.Absolute,
                out var uri)
            || !string.Equals(
                uri.Scheme,
                "wss",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                uri.Host,
                "api.hyperliquid-testnet.xyz",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                uri.AbsolutePath,
                "/ws",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Hyperliquid websocket is restricted to the official testnet endpoint.");
        }

        var address = _options.UserAddress?.Trim();

        if (string.IsNullOrWhiteSpace(address)
            || address.Length != 42
            || !address.StartsWith(
                "0x",
                StringComparison.OrdinalIgnoreCase)
            || !address.AsSpan(2).ToString().All(
                Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                "Hyperliquid websocket requires a valid 42-character testnet UserAddress.");
        }
    }
}
