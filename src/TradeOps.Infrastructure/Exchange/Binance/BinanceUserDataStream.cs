using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;

namespace TradeOps.Infrastructure.Exchange.Binance;

public sealed class BinanceUserDataStream : IExchangeEventStream
{
    private static readonly HashSet<string> AllowedRestHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "testnet.binancefuture.com",
            "demo-fapi.binance.com"
        };

    private static readonly HashSet<string> AllowedWebSocketHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "stream.binancefuture.com",
            "fstream.binancefuture.com"
        };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BinanceOptions _options;
    private readonly ILogger<BinanceUserDataStream> _logger;

    public BinanceUserDataStream(
        IHttpClientFactory httpClientFactory,
        BinanceOptions options,
        ILogger<BinanceUserDataStream> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;

        ValidateEndpoints();
    }

    public bool IsEnabled => true;

    public async Task RunAsync(
        Func<ExchangeOrderUpdate, CancellationToken, Task> onOrderUpdate,
        Func<ExchangeExecutionUpdate, CancellationToken, Task> onExecutionUpdate,
        CancellationToken cancellationToken = default)
    {
        ValidateCredentials();

        var listenKey = await StartListenKeyAsync(cancellationToken);

        try
        {
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromMinutes(2);

            var streamUri = BuildStreamUri(listenKey);
            await socket.ConnectAsync(streamUri, cancellationToken);

            _logger.LogInformation(
                "Connected to Binance USD-M Futures testnet user-data stream.");

            using var linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var receiveTask = ReceiveLoopAsync(
                socket,
                onOrderUpdate,
                onExecutionUpdate,
                linkedCts.Token);

            var keepaliveTask = KeepaliveLoopAsync(
                listenKey,
                linkedCts.Token);

            var completed = await Task.WhenAny(receiveTask, keepaliveTask);
            linkedCts.Cancel();

            await completed;

            try
            {
                await Task.WhenAll(receiveTask, keepaliveTask);
            }
            catch (OperationCanceledException)
                when (linkedCts.IsCancellationRequested)
            {
            }
        }
        finally
        {
            await TryCloseListenKeyAsync();
        }
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

            if (IsListenKeyExpired(json))
            {
                throw new InvalidOperationException(
                    "Binance user-data listenKey expired.");
            }

            foreach (var update in
                     BinanceUserDataMessageParser.ParseOrderUpdates(json))
            {
                await onOrderUpdate(update, cancellationToken);
            }

            foreach (var update in
                     BinanceUserDataMessageParser.ParseExecutionUpdates(json))
            {
                await onExecutionUpdate(update, cancellationToken);
            }
        }
    }

    private async Task KeepaliveLoopAsync(
        string listenKey,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMinutes(
            Math.Clamp(_options.ListenKeyKeepaliveMinutes, 1, 55));

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(interval, cancellationToken);
            _ = await SendListenKeyRequestAsync(
                HttpMethod.Put,
                cancellationToken);

            _logger.LogDebug(
                "Binance Futures user-data listenKey keepalive succeeded.");
        }
    }

    private async Task<string> StartListenKeyAsync(
        CancellationToken cancellationToken)
    {
        var json = await SendListenKeyRequestAsync(
            HttpMethod.Post,
            cancellationToken);

        var response = JsonSerializer.Deserialize<BinanceListenKeyDto>(json)
            ?? throw new InvalidOperationException(
                "Binance listenKey response was empty or invalid.");

        if (string.IsNullOrWhiteSpace(response.ListenKey))
        {
            throw new InvalidOperationException(
                "Binance listenKey response did not contain a listenKey.");
        }

        return response.ListenKey;
    }

    private async Task<string> SendListenKeyRequestAsync(
        HttpMethod method,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}/fapi/v1/listenKey";

        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", _options.ApiKey);

        var client = _httpClientFactory.CreateClient(
            BinanceFuturesExchangeClient.HttpClientName);

        using var response = await client.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Binance listenKey request returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        return json;
    }

    private async Task TryCloseListenKeyAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            _ = await SendListenKeyRequestAsync(
                HttpMethod.Delete,
                timeout.Token);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Could not close Binance Futures user-data listenKey cleanly.");
        }
    }

    private Uri BuildStreamUri(string listenKey) =>
        new(
            $"{_options.PrivateWebSocketBaseUrl.TrimEnd('/')}/ws/{Uri.EscapeDataString(listenKey)}");

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
                    $"Binance user-data websocket closed: {result.CloseStatus} {result.CloseStatusDescription}");
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool IsListenKeyExpired(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        return root.TryGetProperty("e", out var eventType)
            && string.Equals(
                eventType.GetString(),
                "listenKeyExpired",
                StringComparison.OrdinalIgnoreCase);
    }

    private void ValidateEndpoints()
    {
        if (!Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var restUri)
            || restUri.Scheme != Uri.UriSchemeHttps
            || !AllowedRestHosts.Contains(restUri.Host))
        {
            throw new InvalidOperationException(
                "Binance user-data stream is restricted to approved non-production Futures REST hosts.");
        }

        if (!Uri.TryCreate(
                _options.PrivateWebSocketBaseUrl,
                UriKind.Absolute,
                out var webSocketUri)
            || !string.Equals(
                webSocketUri.Scheme,
                "wss",
                StringComparison.OrdinalIgnoreCase)
            || !AllowedWebSocketHosts.Contains(webSocketUri.Host))
        {
            throw new InvalidOperationException(
                "Binance user-data stream is restricted to approved non-production Futures websocket hosts.");
        }
    }

    private void ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)
            || string.IsNullOrWhiteSpace(_options.ApiSecret))
        {
            throw new InvalidOperationException(
                "Binance Futures testnet user-data stream requires ApiKey and ApiSecret configuration.");
        }
    }
}
