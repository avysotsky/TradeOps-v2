using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Bitget;

public sealed class BitgetDemoExchangeClient
    : IExchangeClient, IExchangeConnectionManager
{
    public const string HttpClientName = "bitget-demo";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> FuturesCategories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "USDT-FUTURES",
            "USDC-FUTURES",
            "COIN-FUTURES"
        };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BitgetOptions _options;
    private readonly ILogger<BitgetDemoExchangeClient> _logger;

    private volatile bool _isConnected;

    public BitgetDemoExchangeClient(
        IHttpClientFactory httpClientFactory,
        BitgetOptions options,
        ILogger<BitgetDemoExchangeClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;

        ValidateConfiguration();
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
                "ExchangeReadinessSucceeded for Bitget Demo.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _isConnected = false;
            _logger.LogError(
                exception,
                "ExchangeReadinessFailed for Bitget Demo.");
            throw;
        }
    }

    public async Task<AccountInfo> GetAccountAsync(
        CancellationToken cancellationToken = default)
    {
        var data = await SendPrivateAsync(
            HttpMethod.Get,
            "/api/v3/account/assets",
            [],
            body: null,
            cancellationToken);

        if (!data.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Bitget Demo account response did not contain assets.");
        }

        var currency = _options.AccountCurrency
            .Trim()
            .ToUpperInvariant();

        var asset = assets
            .EnumerateArray()
            .FirstOrDefault(item =>
                string.Equals(
                    ReadString(item, "coin"),
                    currency,
                    StringComparison.OrdinalIgnoreCase));

        if (asset.ValueKind == JsonValueKind.Undefined)
        {
            return new AccountInfo
            {
                Currency = currency,
                Balance = 0m,
                Equity = 0m,
                AvailableBalance = 0m
            };
        }

        return new AccountInfo
        {
            Currency = currency,
            Balance = ReadDecimal(asset, "balance"),
            Equity = ReadDecimal(asset, "equity"),
            AvailableBalance = ReadDecimal(asset, "available")
        };
    }

    public async Task<IReadOnlyCollection<Position>> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        var data = await SendPrivateAsync(
            HttpMethod.Get,
            "/api/v3/position/current-position",
            [
                Pair("category", NormalizeCategory())
            ],
            body: null,
            cancellationToken);

        if (!data.TryGetProperty("list", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Position>();
        }

        return list
            .EnumerateArray()
            .Where(item => Math.Abs(ReadDecimal(item, "total")) > 0m)
            .Select(MapPosition)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(
        CancellationToken cancellationToken = default)
    {
        var data = await SendPrivateAsync(
            HttpMethod.Get,
            "/api/v3/trade/unfilled-orders",
            [
                Pair("category", NormalizeCategory()),
                Pair("limit", "100")
            ],
            body: null,
            cancellationToken);

        if (!data.TryGetProperty("list", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Order>();
        }

        return list
            .EnumerateArray()
            .Select(MapOrder)
            .ToArray();
    }

    public async Task<OrderResult> PlaceOrderAsync(
        PlaceOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateSymbol(request.Symbol);

        if (request.Quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Bitget order quantity must be positive.");
        }

        if (request.OrderType == OrderType.Limit
            && request.Price is null)
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
                $"Bitget Demo adapter does not support TradeOps order type {request.OrderType}.");
        }

        var clientOid = BitgetClientOrderIdFactory.Create(
            request.ClientOrderId);

        var payload = new Dictionary<string, object?>
        {
            ["category"] = NormalizeCategory(),
            ["symbol"] = request.Symbol.Trim().ToUpperInvariant(),
            ["clientOid"] = clientOid,
            ["side"] = request.Side == OrderSide.Buy ? "buy" : "sell",
            ["orderType"] = request.OrderType == OrderType.Market
                ? "market"
                : "limit",
            ["qty"] = FormatDecimal(request.Quantity),
            ["timeInForce"] = "gtc",
            ["marginMode"] = NormalizeMarginMode()
        };

        if (request.Price is not null)
        {
            payload["price"] = FormatDecimal(request.Price.Value);
        }

        _logger.LogInformation(
            "OrderPlacementRequested for {ClientOrderId}: {Side} {Quantity} {Symbol} on Bitget Demo.",
            request.ClientOrderId,
            request.Side,
            request.Quantity,
            request.Symbol);

        try
        {
            var data = await SendPrivateAsync(
                HttpMethod.Post,
                "/api/v3/trade/place-order",
                [],
                payload,
                cancellationToken);

            var orderId = ReadString(data, "orderId");

            if (string.IsNullOrWhiteSpace(orderId))
            {
                throw new InvalidOperationException(
                    "Bitget Demo place-order response did not contain orderId.");
            }

            return new OrderResult(
                orderId,
                request.ClientOrderId,
                OrderStatus.Accepted,
                0m,
                null);
        }
        catch (BitgetApiException exception)
        {
            _logger.LogWarning(
                exception,
                "Bitget Demo explicitly rejected order {ClientOrderId}.",
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
                $"Bitget Demo order placement timed out for {request.ClientOrderId}.",
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
                $"Bitget Demo order placement outcome is ambiguous for {request.ClientOrderId}.",
                exception);
        }
    }

    public Task CancelOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default) =>
        CancelOrderCoreAsync(
            exchangeOrderId,
            cancellationToken);

    public Task CancelOrderAsync(
        string exchangeOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        _ = symbol;
        return CancelOrderCoreAsync(
            exchangeOrderId,
            cancellationToken);
    }

    public Task<Order?> GetOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default) =>
        GetOrderInfoAsync(
            "orderId",
            exchangeOrderId,
            originalClientOrderId: null,
            cancellationToken);

    public Task<Order?> GetOrderAsync(
        string exchangeOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        _ = symbol;

        return GetOrderAsync(
            exchangeOrderId,
            cancellationToken);
    }

    public Task<Order?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default) =>
        GetOrderInfoAsync(
            "clientOid",
            BitgetClientOrderIdFactory.Create(clientOrderId),
            clientOrderId,
            cancellationToken);

    public Task<Order?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        _ = symbol;

        return GetOrderByClientOrderIdAsync(
            clientOrderId,
            cancellationToken);
    }

    internal static void ValidateSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)
            || symbol.Length < 6
            || !symbol.All(char.IsLetterOrDigit))
        {
            throw new ArgumentException(
                "Bitget Demo adapter requires a native alphanumeric futures symbol such as BTCUSDT.",
                nameof(symbol));
        }
    }

    private async Task CancelOrderCoreAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(exchangeOrderId))
        {
            throw new ArgumentException(
                "Bitget order id is required.",
                nameof(exchangeOrderId));
        }

        _ = await SendPrivateAsync(
            HttpMethod.Post,
            "/api/v3/trade/cancel-order",
            [],
            new Dictionary<string, string>
            {
                ["category"] = NormalizeCategory(),
                ["orderId"] = exchangeOrderId
            },
            cancellationToken);
    }

    private async Task<Order?> GetOrderInfoAsync(
        string lookupKey,
        string lookupValue,
        string? originalClientOrderId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(lookupValue))
        {
            return null;
        }

        try
        {
            var data = await SendPrivateAsync(
                HttpMethod.Get,
                "/api/v3/trade/order-info",
                [
                    Pair(lookupKey, lookupValue)
                ],
                body: null,
                cancellationToken);

            if (data.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var order = MapOrder(data);

            if (!string.IsNullOrWhiteSpace(originalClientOrderId))
            {
                order.ClientOrderId = originalClientOrderId;
            }

            return order;
        }
        catch (BitgetApiException exception)
            when (IsOrderNotFound(exception.Code))
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
            var existing =
                await GetOrderByClientOrderIdAsync(
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
                "Bitget Demo ambiguous placement recovery failed for {ClientOrderId}.",
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
        var requestPath = string.IsNullOrWhiteSpace(query)
            ? path
            : $"{path}?{query}";

        var bodyJson = body is null
            ? string.Empty
            : JsonSerializer.Serialize(
                body,
                JsonOptions);

        var timestamp =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var signature = BitgetSigner.Sign(
            timestamp,
            method.Method,
            requestPath,
            bodyJson,
            _options.ApiSecret);

        using var request = new HttpRequestMessage(
            method,
            $"{_options.BaseUrl.TrimEnd('/')}{requestPath}");

        request.Headers.TryAddWithoutValidation(
            "ACCESS-KEY",
            _options.ApiKey);
        request.Headers.TryAddWithoutValidation(
            "ACCESS-SIGN",
            signature);
        request.Headers.TryAddWithoutValidation(
            "ACCESS-TIMESTAMP",
            timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(
            "ACCESS-PASSPHRASE",
            _options.Passphrase);

        // Hard safety boundary: this provider always uses Bitget Demo.
        request.Headers.TryAddWithoutValidation(
            "paptrading",
            "1");

        if (body is not null)
        {
            request.Content = new StringContent(
                bodyJson,
                Encoding.UTF8,
                "application/json");
        }

        var client =
            _httpClientFactory.CreateClient(HttpClientName);

        try
        {
            using var response = await client.SendAsync(
                request,
                cancellationToken);

            var json = await response.Content.ReadAsStringAsync(
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _isConnected = false;
                throw new HttpRequestException(
                    $"Bitget Demo returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var code = ReadString(root, "code");

            if (!string.Equals(
                    code,
                    "00000",
                    StringComparison.Ordinal))
            {
                throw new BitgetApiException(
                    code,
                    ReadString(root, "msg"));
            }

            if (!root.TryGetProperty("data", out var data))
            {
                throw new InvalidOperationException(
                    "Bitget Demo response did not contain data.");
            }

            _isConnected = true;
            return data.Clone();
        }
        catch (HttpRequestException)
        {
            _isConnected = false;
            throw;
        }
    }

    private Position MapPosition(JsonElement item)
    {
        var total = ReadDecimal(item, "total");
        var posSide = ReadString(item, "posSide");

        var side =
            posSide.Equals(
                "short",
                StringComparison.OrdinalIgnoreCase)
            || total < 0m
                ? OrderSide.Sell
                : OrderSide.Buy;

        return new Position
        {
            Symbol = ReadString(item, "symbol"),
            Side = side,
            Quantity = Math.Abs(total),
            AverageEntryPrice =
                ReadDecimal(item, "avgPrice"),
            MarkPrice =
                ReadDecimal(item, "markPrice"),
            UnrealizedPnL =
                ReadDecimal(item, "unrealisedPnl")
        };
    }

    private Order MapOrder(JsonElement item)
    {
        var requested =
            ReadDecimal(item, "qty");

        var filled =
            Math.Min(
                requested,
                ReadDecimal(item, "cumExecQty"));

        return new Order
        {
            Id = Guid.NewGuid(),
            ExchangeOrderId =
                ReadString(item, "orderId"),
            ClientOrderId =
                string.IsNullOrWhiteSpace(
                    ReadString(item, "clientOid"))
                    ? $"bitget-{ReadString(item, "orderId")}"
                    : ReadString(item, "clientOid"),
            Symbol =
                ReadString(item, "symbol"),
            Side =
                ReadString(item, "side")
                    .Equals(
                        "sell",
                        StringComparison.OrdinalIgnoreCase)
                    ? OrderSide.Sell
                    : OrderSide.Buy,
            OrderType =
                ReadString(item, "orderType")
                    .Equals(
                        "market",
                        StringComparison.OrdinalIgnoreCase)
                    ? OrderType.Market
                    : OrderType.Limit,
            RequestedQuantity = requested,
            FilledQuantity = filled,
            AverageFillPrice =
                ReadPositiveNullableDecimal(
                    item,
                    "avgPrice"),
            Price =
                ReadPositiveNullableDecimal(
                    item,
                    "price"),
            Status =
                MapOrderStatus(
                    ReadString(item, "orderStatus")),
            CreatedAt =
                ReadTimestamp(item, "createdTime"),
            UpdatedAt =
                ReadTimestamp(
                    item,
                    "updatedTime",
                    ReadTimestamp(item, "createdTime"))
        };
    }

    private static OrderStatus MapOrderStatus(
        string status) =>
        status.ToLowerInvariant() switch
        {
            "live" => OrderStatus.Accepted,
            "new" => OrderStatus.Accepted,
            "partially_filled" =>
                OrderStatus.PartiallyFilled,
            "filled" => OrderStatus.Filled,
            "cancelled" => OrderStatus.Cancelled,
            "canceled" => OrderStatus.Cancelled,
            "rejected" => OrderStatus.Rejected,
            _ => OrderStatus.Unknown
        };

    private void ValidateConfiguration()
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
                "api.bitget.com",
                StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Trim('/') != string.Empty)
        {
            throw new InvalidOperationException(
                "Bitget Demo adapter is restricted to the official api.bitget.com REST host.");
        }

        if (!FuturesCategories.Contains(
                NormalizeCategory()))
        {
            throw new InvalidOperationException(
                "Bitget Demo Category must be USDT-FUTURES, USDC-FUTURES, or COIN-FUTURES.");
        }

        var marginMode =
            NormalizeMarginMode();

        if (marginMode is not ("crossed" or "isolated"))
        {
            throw new InvalidOperationException(
                "Bitget Demo MarginMode must be 'crossed' or 'isolated'.");
        }
    }

    private void ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)
            || string.IsNullOrWhiteSpace(_options.ApiSecret)
            || string.IsNullOrWhiteSpace(_options.Passphrase))
        {
            throw new InvalidOperationException(
                "Bitget Demo ApiKey, ApiSecret and Passphrase are required.");
        }
    }

    private string NormalizeCategory() =>
        _options.Category
            .Trim()
            .ToUpperInvariant();

    private string NormalizeMarginMode() =>
        _options.MarginMode
            .Trim()
            .ToLowerInvariant();

    private static bool IsOrderNotFound(
        string code) =>
        code is "22001" or "22002" or "40017";

    private static string BuildQuery(
        IEnumerable<KeyValuePair<string, string?>> parameters) =>
        string.Join(
            "&",
            parameters
                .Where(item => item.Value is not null)
                .OrderBy(
                    item => item.Key,
                    StringComparer.Ordinal)
                .Select(item =>
                    $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value ?? string.Empty)}"));

    private static KeyValuePair<string, string?> Pair(
        string key,
        string? value) =>
        new(key, value);

    private static string FormatDecimal(
        decimal value) =>
        value.ToString(
            "0.############################",
            CultureInfo.InvariantCulture);

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

    private static decimal ReadDecimal(
        JsonElement element,
        string name)
    {
        var raw = ReadString(
            element,
            name);

        return decimal.TryParse(
            raw,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0m;
    }

    private static decimal? ReadPositiveNullableDecimal(
        JsonElement element,
        string name)
    {
        var value = ReadDecimal(
            element,
            name);

        return value > 0m
            ? value
            : null;
    }

    private static DateTimeOffset ReadTimestamp(
        JsonElement element,
        string name,
        DateTimeOffset? fallback = null)
    {
        var raw = ReadString(
            element,
            name);

        return long.TryParse(
            raw,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var milliseconds)
            && milliseconds > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(
                milliseconds)
            : fallback ?? DateTimeOffset.UtcNow;
    }
}
