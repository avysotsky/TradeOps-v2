using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Gate;

public sealed class GateFuturesTestnetExchangeClient
    : IExchangeClient, IExchangeConnectionManager
{
    public const string HttpClientName = "gate-futures-testnet";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GateOptions _options;
    private readonly ILogger<GateFuturesTestnetExchangeClient> _logger;
    private volatile bool _isConnected;

    public GateFuturesTestnetExchangeClient(
        IHttpClientFactory httpClientFactory,
        GateOptions options,
        ILogger<GateFuturesTestnetExchangeClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        ValidateTestnetConfiguration();
    }

    public bool IsConnected => _isConnected;

    public async Task EnsureConnectedAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await GetAccountAsync(cancellationToken);
            _isConnected = true;
            _logger.LogInformation(
                "ExchangeReadinessSucceeded for Gate Futures TestNet.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _isConnected = false;
            _logger.LogError(
                exception,
                "ExchangeReadinessFailed for Gate Futures TestNet.");
            throw;
        }
    }

    public async Task<AccountInfo> GetAccountAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendPrivateAsync(
            HttpMethod.Get,
            $"/futures/{NormalizeSettle()}/accounts",
            [],
            body: null,
            cancellationToken);

        var balance = ReadDecimal(result, "total");
        var unrealized = ReadDecimal(result, "unrealised_pnl");

        return new AccountInfo
        {
            Currency = NormalizeSettle().ToUpperInvariant(),
            Balance = balance,
            Equity = balance + unrealized,
            AvailableBalance = ReadDecimal(result, "available")
        };
    }

    public async Task<IReadOnlyCollection<Position>> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendPrivateAsync(
            HttpMethod.Get,
            $"/futures/{NormalizeSettle()}/positions",
            [Pair("holding", "true")],
            body: null,
            cancellationToken);

        if (result.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Position>();
        }

        return result
            .EnumerateArray()
            .Where(item => ReadDecimal(item, "size") != 0m)
            .Select(MapPosition)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendPrivateAsync(
            HttpMethod.Get,
            $"/futures/{NormalizeSettle()}/orders",
            [
                Pair("status", "open"),
                Pair("limit", "100")
            ],
            body: null,
            cancellationToken);

        if (result.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Order>();
        }

        return result
            .EnumerateArray()
            .Select(MapOrder)
            .ToArray();
    }

    public async Task<OrderResult> PlaceOrderAsync(
        PlaceOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateContract(request.Symbol);
        var contracts = NormalizeContracts(request.Quantity);
        var text = GateClientOrderIdFactory.Create(request.ClientOrderId);

        if (request.OrderType == OrderType.Limit && request.Price is null)
        {
            return new OrderResult(
                null,
                request.ClientOrderId,
                OrderStatus.Rejected,
                0m,
                null);
        }

        if (request.OrderType is not (OrderType.Market or OrderType.Limit))
        {
            throw new NotSupportedException(
                $"Gate adapter does not support TradeOps order type {request.OrderType}.");
        }

        var signedSize = request.Side == OrderSide.Buy
            ? contracts
            : -contracts;

        var payload = new Dictionary<string, object?>
        {
            ["contract"] = request.Symbol.Trim().ToUpperInvariant(),
            ["size"] = signedSize.ToString(CultureInfo.InvariantCulture),
            ["price"] = request.OrderType == OrderType.Market
                ? "0"
                : FormatDecimal(request.Price!.Value),
            ["tif"] = request.OrderType == OrderType.Market
                ? "ioc"
                : "gtc",
            ["text"] = text
        };

        _logger.LogInformation(
            "OrderPlacementRequested for {ClientOrderId}: {Side} {Quantity} contracts {Contract} on Gate Futures TestNet.",
            request.ClientOrderId,
            request.Side,
            contracts,
            request.Symbol);

        try
        {
            var result = await SendPrivateAsync(
                HttpMethod.Post,
                $"/futures/{NormalizeSettle()}/orders",
                [],
                payload,
                cancellationToken);

            var order = MapOrder(result);
            return new OrderResult(
                order.ExchangeOrderId,
                request.ClientOrderId,
                order.Status,
                order.FilledQuantity,
                order.AverageFillPrice);
        }
        catch (GateApiException exception)
        {
            _logger.LogWarning(
                exception,
                "Gate Futures TestNet explicitly rejected order {ClientOrderId}.",
                request.ClientOrderId);

            return new OrderResult(
                null,
                request.ClientOrderId,
                OrderStatus.Rejected,
                0m,
                null);
        }
        catch (TaskCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            var recovered = await TryRecoverPlacementAsync(
                request,
                cancellationToken);

            if (recovered is not null)
            {
                return recovered;
            }

            throw new TimeoutException(
                $"Gate Futures TestNet order placement timed out for {request.ClientOrderId}.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            var recovered = await TryRecoverPlacementAsync(
                request,
                cancellationToken);

            if (recovered is not null)
            {
                return recovered;
            }

            throw new TimeoutException(
                $"Gate Futures TestNet order placement outcome is ambiguous for {request.ClientOrderId}.",
                exception);
        }
    }

    public Task CancelOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default) =>
        CancelOrderCoreAsync(exchangeOrderId, cancellationToken);

    public Task CancelOrderAsync(
        string exchangeOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        _ = symbol;
        return CancelOrderCoreAsync(exchangeOrderId, cancellationToken);
    }

    public Task<Order?> GetOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default) =>
        GetOrderCoreAsync(
            exchangeOrderId,
            originalClientOrderId: null,
            cancellationToken);

    public Task<Order?> GetOrderAsync(
        string exchangeOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        _ = symbol;
        return GetOrderAsync(exchangeOrderId, cancellationToken);
    }

    public Task<Order?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default) =>
        GetOrderCoreAsync(
            GateClientOrderIdFactory.Create(clientOrderId),
            clientOrderId,
            cancellationToken);

    public Task<Order?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        _ = symbol;
        return GetOrderByClientOrderIdAsync(clientOrderId, cancellationToken);
    }

    internal static void ValidateContract(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)
            || !symbol.Contains('_', StringComparison.Ordinal)
            || symbol.Any(character =>
                !(char.IsLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException(
                "Gate Futures adapter requires a native contract name such as BTC_USDT.",
                nameof(symbol));
        }
    }

    internal static long NormalizeContracts(decimal quantity)
    {
        if (quantity <= 0m
            || decimal.Truncate(quantity) != quantity
            || quantity > long.MaxValue)
        {
            throw new ArgumentException(
                "Gate Futures quantity must be a positive whole number of contracts.",
                nameof(quantity));
        }

        return decimal.ToInt64(quantity);
    }

    private async Task CancelOrderCoreAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(exchangeOrderId))
        {
            throw new ArgumentException(
                "Gate order id is required.",
                nameof(exchangeOrderId));
        }

        _ = await SendPrivateAsync(
            HttpMethod.Delete,
            $"/futures/{NormalizeSettle()}/orders/{Uri.EscapeDataString(exchangeOrderId)}",
            [],
            body: null,
            cancellationToken);
    }

    private async Task<Order?> GetOrderCoreAsync(
        string orderIdOrText,
        string? originalClientOrderId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(orderIdOrText))
        {
            return null;
        }

        try
        {
            var result = await SendPrivateAsync(
                HttpMethod.Get,
                $"/futures/{NormalizeSettle()}/orders/{Uri.EscapeDataString(orderIdOrText)}",
                [],
                body: null,
                cancellationToken);

            var order = MapOrder(result);
            if (!string.IsNullOrWhiteSpace(originalClientOrderId))
            {
                order.ClientOrderId = originalClientOrderId;
            }

            return order;
        }
        catch (GateApiException exception)
            when (string.Equals(
                exception.Label,
                "ORDER_NOT_FOUND",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }

    private async Task<OrderResult?> TryRecoverPlacementAsync(
        PlaceOrderRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await GetOrderByClientOrderIdAsync(
                request.ClientOrderId,
                cancellationToken);

            return existing is null
                ? null
                : new OrderResult(
                    existing.ExchangeOrderId,
                    request.ClientOrderId,
                    existing.Status,
                    existing.FilledQuantity,
                    existing.AverageFillPrice);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Gate Futures TestNet ambiguous placement recovery failed for {ClientOrderId}.",
                request.ClientOrderId);
            return null;
        }
    }

    private async Task<JsonElement> SendPrivateAsync(
        HttpMethod method,
        string path,
        IEnumerable<KeyValuePair<string, string?>> queryParameters,
        object? body,
        CancellationToken cancellationToken)
    {
        ValidateCredentials();

        var query = BuildQuery(queryParameters);
        var bodyJson = body is null
            ? string.Empty
            : JsonSerializer.Serialize(body, JsonOptions);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requestPath = "/api/v4" + path;

        var signature = GateSigner.Sign(
            method.Method,
            requestPath,
            query,
            bodyJson,
            timestamp,
            _options.ApiSecret);

        var url = BuildUrl(path, query);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("KEY", _options.ApiKey);
        request.Headers.TryAddWithoutValidation(
            "Timestamp",
            timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("SIGN", signature);

        if (body is not null)
        {
            request.Content = new StringContent(
                bodyJson,
                Encoding.UTF8,
                "application/json");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);

        try
        {
            using var response = await client.SendAsync(
                request,
                cancellationToken);

            var json = await response.Content.ReadAsStringAsync(
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode >= 500)
                {
                    _isConnected = false;
                }

                try
                {
                    using var errorDoc = JsonDocument.Parse(json);
                    var root = errorDoc.RootElement;
                    throw new GateApiException(
                        ReadString(root, "label"),
                        ReadString(root, "message"));
                }
                catch (JsonException)
                {
                    throw new HttpRequestException(
                        $"Gate Futures TestNet returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
                }
            }

            using var document = JsonDocument.Parse(json);
            _isConnected = true;
            return document.RootElement.Clone();
        }
        catch (HttpRequestException)
        {
            _isConnected = false;
            throw;
        }
    }

    private string BuildUrl(string path, string query)
    {
        var url = $"{_options.BaseUrl.TrimEnd('/')}{path}";
        return string.IsNullOrWhiteSpace(query)
            ? url
            : $"{url}?{query}";
    }

    private Position MapPosition(JsonElement item)
    {
        var size = ReadDecimal(item, "size");

        return new Position
        {
            Symbol = ReadString(item, "contract"),
            Side = size < 0m ? OrderSide.Sell : OrderSide.Buy,
            Quantity = Math.Abs(size),
            AverageEntryPrice = ReadDecimal(item, "entry_price"),
            MarkPrice = ReadDecimal(item, "mark_price"),
            UnrealizedPnL = ReadDecimal(item, "unrealised_pnl")
        };
    }

    private Order MapOrder(JsonElement item)
    {
        var signedSize = ReadDecimal(item, "size");
        var requested = Math.Abs(signedSize);
        var left = Math.Abs(ReadDecimal(item, "left"));
        var filled = Math.Max(0m, requested - Math.Min(requested, left));

        var status = ReadString(item, "status");
        var finishAs = ReadString(item, "finish_as");

        var mappedStatus = status.Equals(
                "open",
                StringComparison.OrdinalIgnoreCase)
            ? (filled > 0m
                ? OrderStatus.PartiallyFilled
                : OrderStatus.Accepted)
            : MapFinishedStatus(finishAs);

        var price = ReadDecimal(item, "price");
        var fillPrice = ReadDecimal(item, "fill_price");

        return new Order
        {
            Id = Guid.NewGuid(),
            ExchangeOrderId = ReadString(item, "id"),
            ClientOrderId =
                string.IsNullOrWhiteSpace(ReadString(item, "text"))
                    ? $"gate-{ReadString(item, "id")}"
                    : ReadString(item, "text"),
            Symbol = ReadString(item, "contract"),
            Side = signedSize < 0m ? OrderSide.Sell : OrderSide.Buy,
            OrderType = price == 0m
                ? OrderType.Market
                : OrderType.Limit,
            RequestedQuantity = requested,
            FilledQuantity = filled,
            AverageFillPrice = fillPrice > 0m ? fillPrice : null,
            Price = price > 0m ? price : null,
            Status = mappedStatus,
            CreatedAt = ReadUnixSeconds(item, "create_time"),
            UpdatedAt = ReadUnixSeconds(
                item,
                "update_time",
                ReadUnixSeconds(item, "create_time"))
        };
    }

    private static OrderStatus MapFinishedStatus(string finishAs) =>
        finishAs.ToLowerInvariant() switch
        {
            "filled" => OrderStatus.Filled,
            "cancelled" => OrderStatus.Cancelled,
            "liquidated" => OrderStatus.Cancelled,
            "ioc" => OrderStatus.Cancelled,
            "auto_deleveraged" => OrderStatus.Cancelled,
            "reduce_only" => OrderStatus.Cancelled,
            "position_closed" => OrderStatus.Cancelled,
            "reduce_out" => OrderStatus.Cancelled,
            "stp" => OrderStatus.Cancelled,
            _ => OrderStatus.Unknown
        };

    private void ValidateTestnetConfiguration()
    {
        if (!Uri.TryCreate(
                _options.BaseUrl,
                UriKind.Absolute,
                out var uri)
            || !string.Equals(
                uri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                uri.Host,
                "api-testnet.gateapi.io",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                uri.AbsolutePath.TrimEnd('/'),
                "/api/v4",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Gate Futures adapter is restricted to the official TestNet API endpoint.");
        }

        _ = NormalizeSettle();
    }

    private void ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)
            || string.IsNullOrWhiteSpace(_options.ApiSecret))
        {
            throw new InvalidOperationException(
                "Gate Futures TestNet ApiKey and ApiSecret are required.");
        }
    }

    private string NormalizeSettle()
    {
        var settle = _options.Settle.Trim().ToLowerInvariant();

        if (settle is not ("usdt" or "btc" or "usd1"))
        {
            throw new InvalidOperationException(
                "Gate Futures settle must be usdt, btc, or usd1.");
        }

        return settle;
    }

    private static string BuildQuery(
        IEnumerable<KeyValuePair<string, string?>> parameters) =>
        string.Join(
            "&",
            parameters
                .Where(item => item.Value is not null)
                .Select(item =>
                    $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value ?? string.Empty)}"));

    private static KeyValuePair<string, string?> Pair(
        string key,
        string? value) =>
        new(key, value);

    private static string FormatDecimal(decimal value) =>
        value.ToString(
            "0.############################",
            CultureInfo.InvariantCulture);

    private static string ReadString(JsonElement element, string name)
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

    private static decimal ReadDecimal(JsonElement element, string name)
    {
        var raw = ReadString(element, name);
        return decimal.TryParse(
            raw,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0m;
    }

    private static DateTimeOffset ReadUnixSeconds(
        JsonElement element,
        string name,
        DateTimeOffset? fallback = null)
    {
        var raw = ReadString(element, name);

        if (double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var seconds)
            && seconds > 0)
        {
            var milliseconds = checked((long)(seconds * 1000d));
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }

        return fallback ?? DateTimeOffset.UtcNow;
    }
}
