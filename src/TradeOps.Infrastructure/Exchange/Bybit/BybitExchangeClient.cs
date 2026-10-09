using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Bybit;

public sealed class BybitExchangeClient : IExchangeClient, IExchangeConnectionManager
{
    public const string HttpClientName = "bybit";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BybitOptions _options;
    private readonly ILogger<BybitExchangeClient> _logger;
    private volatile bool _isConnected;

    public BybitExchangeClient(
        IHttpClientFactory httpClientFactory,
        BybitOptions options,
        ILogger<BybitExchangeClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        ValidateTestnetBaseUrl(options.BaseUrl);
    }

    public bool IsConnected => _isConnected;

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await GetAccountAsync(cancellationToken);
            _isConnected = true;
            _logger.LogInformation("ExchangeAuthenticationSucceeded for Bybit testnet.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _isConnected = false;
            _logger.LogError(exception, "ExchangeAuthenticationFailed for Bybit testnet.");
            throw;
        }
    }

    public async Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        var query = BuildQuery(("accountType", _options.AccountType));
        var result = await SendPrivateGetAsync<BybitWalletResult>(
            "/v5/account/wallet-balance",
            query,
            cancellationToken);

        var account = result.List.FirstOrDefault()
            ?? throw new BybitApiException(-1, "Wallet response did not contain an account record.");

        _isConnected = true;

        return new AccountInfo
        {
            Currency = "USD",
            Balance = ParseDecimal(account.TotalWalletBalance),
            Equity = ParseDecimal(account.TotalEquity),
            AvailableBalance = ParseDecimal(account.TotalAvailableBalance)
        };
    }

    public async Task<IReadOnlyCollection<Position>> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        var query = BuildQuery(
            ("category", _options.Category),
            ("settleCoin", _options.SettleCoin),
            ("limit", "200"));

        var result = await SendPrivateGetAsync<BybitPositionResult>(
            "/v5/position/list",
            query,
            cancellationToken);

        return result.List
            .Where(position => ParseDecimal(position.Size) > 0m)
            .Select(MapPosition)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(
        CancellationToken cancellationToken = default)
    {
        var query = BuildQuery(
            ("category", _options.Category),
            ("settleCoin", _options.SettleCoin),
            ("openOnly", "0"),
            ("limit", "50"));

        var result = await SendPrivateGetAsync<BybitOrderListResult>(
            "/v5/order/realtime",
            query,
            cancellationToken);

        return result.List.Select(MapOrder).ToArray();
    }

    public async Task<OrderResult> PlaceOrderAsync(
        PlaceOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ClientOrderId.Length > 36)
        {
            throw new ArgumentException(
                "Bybit orderLinkId cannot exceed 36 characters.",
                nameof(request));
        }

        if (request.OrderType == OrderType.Limit && request.Price is null)
        {
            return RejectLocally(request, "Limit orders require Price.");
        }

        BybitInstrumentDto instrument;
        try
        {
            instrument = await GetInstrumentAsync(request.Symbol, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Order {ClientOrderId} was rejected before placement because instrument filters for {Symbol} could not be loaded.",
                request.ClientOrderId,
                request.Symbol);

            return new OrderResult(
                null,
                request.ClientOrderId,
                OrderStatus.Rejected,
                0m,
                null);
        }

        var validationError = BybitOrderValidator.Validate(request, instrument);
        if (validationError is not null)
        {
            return RejectLocally(request, validationError);
        }

        var payload = new Dictionary<string, object?>
        {
            ["category"] = _options.Category,
            ["symbol"] = request.Symbol.ToUpperInvariant(),
            ["side"] = request.Side == OrderSide.Buy ? "Buy" : "Sell",
            ["orderType"] = request.OrderType switch
            {
                OrderType.Market => "Market",
                OrderType.Limit => "Limit",
                _ => throw new NotSupportedException(
                    $"Bybit adapter does not support TradeOps order type {request.OrderType} yet.")
            },
            ["qty"] = FormatDecimal(request.Quantity),
            ["orderLinkId"] = request.ClientOrderId
        };

        if (request.Price is not null)
        {
            payload["price"] = FormatDecimal(request.Price.Value);
        }

        _logger.LogInformation(
            "OrderPlacementRequested for {ClientOrderId}: {Side} {Quantity} {Symbol}.",
            request.ClientOrderId,
            request.Side,
            request.Quantity,
            request.Symbol);

        try
        {
            var result = await SendPrivatePostAsync<BybitOrderAckResult>(
                "/v5/order/create",
                payload,
                cancellationToken);

            _logger.LogInformation(
                "OrderPlacementConfirmed for {ClientOrderId}; exchange order {ExchangeOrderId} acknowledged.",
                request.ClientOrderId,
                result.OrderId);

            return new OrderResult(
                result.OrderId,
                string.IsNullOrWhiteSpace(result.OrderLinkId)
                    ? request.ClientOrderId
                    : result.OrderLinkId,
                OrderStatus.Accepted,
                0m,
                null);
        }
        catch (BybitApiException exception) when (exception.ReturnCode == 10014)
        {
            var existing = await GetOrderByClientOrderIdAsync(
                request.ClientOrderId,
                cancellationToken);

            if (existing is not null)
            {
                return ToOrderResult(existing);
            }

            throw;
        }
        catch (BybitApiException exception)
        {
            _logger.LogWarning(
                exception,
                "Bybit explicitly rejected order {ClientOrderId}.",
                request.ClientOrderId);

            return new OrderResult(
                null,
                request.ClientOrderId,
                OrderStatus.Rejected,
                0m,
                null);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                exception,
                "OrderPlacementAmbiguous for {ClientOrderId} because the Bybit request timed out.",
                request.ClientOrderId);

            throw new TimeoutException(
                $"Bybit order placement timed out for {request.ClientOrderId}.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "OrderPlacementAmbiguous for {ClientOrderId} because the HTTP outcome is unknown.",
                request.ClientOrderId);

            throw new TimeoutException(
                $"Bybit order placement outcome is ambiguous for {request.ClientOrderId}.",
                exception);
        }
    }

    public async Task CancelOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetOrderAsync(exchangeOrderId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Cannot cancel Bybit order '{exchangeOrderId}' because it could not be found.");

        var payload = new Dictionary<string, object?>
        {
            ["category"] = _options.Category,
            ["symbol"] = existing.Symbol,
            ["orderId"] = exchangeOrderId
        };

        _ = await SendPrivatePostAsync<BybitOrderAckResult>(
            "/v5/order/cancel",
            payload,
            cancellationToken);

        _logger.LogInformation(
            "OrderCancelled request acknowledged for exchange order {ExchangeOrderId}.",
            exchangeOrderId);
    }

    public Task<Order?> GetOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default) =>
        GetOrderWithHistoryFallbackAsync("orderId", exchangeOrderId, cancellationToken);

    public Task<Order?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default) =>
        GetOrderWithHistoryFallbackAsync("orderLinkId", clientOrderId, cancellationToken);

    private async Task<Order?> GetOrderWithHistoryFallbackAsync(
        string lookupKey,
        string lookupValue,
        CancellationToken cancellationToken)
    {
        var query = BuildQuery(
            ("category", _options.Category),
            ("settleCoin", _options.SettleCoin),
            (lookupKey, lookupValue));

        var realtime = await SendPrivateGetAsync<BybitOrderListResult>(
            "/v5/order/realtime",
            query,
            cancellationToken);

        var realtimeOrder = realtime.List.FirstOrDefault();
        if (realtimeOrder is not null)
        {
            return MapOrder(realtimeOrder);
        }

        _logger.LogDebug(
            "Bybit order lookup by {LookupKey} missed realtime state; checking order history.",
            lookupKey);

        var history = await SendPrivateGetAsync<BybitOrderListResult>(
            "/v5/order/history",
            query,
            cancellationToken);

        var historicalOrder = history.List.FirstOrDefault();
        return historicalOrder is null ? null : MapOrder(historicalOrder);
    }

    private async Task<BybitInstrumentDto> GetInstrumentAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        var query = BuildQuery(
            ("category", _options.Category),
            ("symbol", symbol.ToUpperInvariant()));

        var result = await SendPublicGetAsync<BybitInstrumentResult>(
            "/v5/market/instruments-info",
            query,
            cancellationToken);

        return result.List.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Bybit did not return instrument information for '{symbol}'.");
    }

    private async Task<T> SendPublicGetAsync<T>(
        string path,
        string query,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUri(path, query));

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Bybit returned HTTP {(int)response.StatusCode} ({response.StatusCode}) for instrument info.");
        }

        var envelope = JsonSerializer.Deserialize<BybitEnvelope<T>>(json, JsonOptions)
            ?? throw new InvalidOperationException("Bybit returned an empty or invalid JSON response.");

        if (envelope.RetCode != 0)
        {
            throw new BybitApiException(envelope.RetCode, envelope.RetMsg);
        }

        return envelope.Result
            ?? throw new InvalidOperationException("Bybit response did not contain a result object.");
    }

    private async Task<T> SendPrivateGetAsync<T>(
        string path,
        string query,
        CancellationToken cancellationToken)
    {
        ValidateCredentials();

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var signature = BybitSigner.CreateHmacSha256Hex(
            _options.ApiSecret,
            timestamp,
            _options.ApiKey,
            _options.RecvWindowMilliseconds,
            query);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUri(path, query));

        AddAuthenticationHeaders(request, timestamp, signature);
        return await SendAsync<T>(request, cancellationToken);
    }

    private async Task<T> SendPrivatePostAsync<T>(
        string path,
        object payload,
        CancellationToken cancellationToken)
    {
        ValidateCredentials();

        var body = JsonSerializer.Serialize(payload, JsonOptions);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var signature = BybitSigner.CreateHmacSha256Hex(
            _options.ApiSecret,
            timestamp,
            _options.ApiKey,
            _options.RecvWindowMilliseconds,
            body);

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(path))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        AddAuthenticationHeaders(request, timestamp, signature);
        return await SendAsync<T>(request, cancellationToken);
    }

    private async Task<T> SendAsync<T>(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _isConnected = false;
                _logger.LogWarning(
                    "ExchangeRequestFailed for {Method} {Path} with HTTP status {StatusCode}.",
                    request.Method,
                    request.RequestUri?.AbsolutePath,
                    (int)response.StatusCode);

                throw new HttpRequestException(
                    $"Bybit returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }

            var envelope = JsonSerializer.Deserialize<BybitEnvelope<T>>(json, JsonOptions)
                ?? throw new InvalidOperationException("Bybit returned an empty or invalid JSON response.");

            if (envelope.RetCode != 0)
            {
                _logger.LogWarning(
                    "ExchangeRequestFailed for {Method} {Path}; Bybit retCode={RetCode}, retMsg={RetMsg}.",
                    request.Method,
                    request.RequestUri?.AbsolutePath,
                    envelope.RetCode,
                    envelope.RetMsg);

                throw new BybitApiException(envelope.RetCode, envelope.RetMsg);
            }

            _isConnected = true;
            return envelope.Result
                ?? throw new InvalidOperationException("Bybit response did not contain a result object.");
        }
        catch (HttpRequestException)
        {
            _isConnected = false;
            throw;
        }
    }

    private void AddAuthenticationHeaders(
        HttpRequestMessage request,
        long timestamp,
        string signature)
    {
        request.Headers.TryAddWithoutValidation("X-BAPI-API-KEY", _options.ApiKey);
        request.Headers.TryAddWithoutValidation("X-BAPI-TIMESTAMP", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-BAPI-RECV-WINDOW", _options.RecvWindowMilliseconds.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-BAPI-SIGN", signature);
    }

    private OrderResult RejectLocally(PlaceOrderRequest request, string reason)
    {
        _logger.LogWarning(
            "Order {ClientOrderId} rejected by local Bybit validation: {Reason}",
            request.ClientOrderId,
            reason);

        return new OrderResult(
            null,
            request.ClientOrderId,
            OrderStatus.Rejected,
            0m,
            null);
    }

    private Position MapPosition(BybitPositionDto position)
    {
        return new Position
        {
            Symbol = position.Symbol,
            Side = MapSide(position.Side),
            Quantity = ParseDecimal(position.Size),
            AverageEntryPrice = ParseDecimal(position.AveragePrice),
            MarkPrice = ParseDecimal(position.MarkPrice),
            UnrealizedPnL = ParseDecimal(position.UnrealizedPnl)
        };
    }

    private static Order MapOrder(BybitOrderDto order)
    {
        return new Order
        {
            Id = Guid.NewGuid(),
            ExchangeOrderId = order.OrderId,
            ClientOrderId = order.OrderLinkId,
            Symbol = order.Symbol,
            Side = MapSide(order.Side),
            OrderType = MapOrderType(order.OrderType),
            RequestedQuantity = ParseDecimal(order.Quantity),
            FilledQuantity = ParseDecimal(order.CumulativeExecutedQuantity),
            AverageFillPrice = ParseNullableDecimal(order.AveragePrice),
            Price = ParseNullableDecimal(order.Price),
            Status = MapOrderStatus(order.OrderStatus),
            CreatedAt = ParseUnixMilliseconds(order.CreatedTime),
            UpdatedAt = ParseUnixMilliseconds(order.UpdatedTime)
        };
    }

    private static OrderResult ToOrderResult(Order order) => new(
        order.ExchangeOrderId,
        order.ClientOrderId,
        order.Status,
        order.FilledQuantity,
        order.AverageFillPrice);

    private static OrderSide MapSide(string side) => side switch
    {
        "Buy" => OrderSide.Buy,
        "Sell" => OrderSide.Sell,
        _ => throw new InvalidOperationException($"Unsupported Bybit side '{side}'.")
    };

    private static OrderType MapOrderType(string orderType) => orderType switch
    {
        "Market" => OrderType.Market,
        "Limit" => OrderType.Limit,
        _ => throw new InvalidOperationException($"Unsupported Bybit order type '{orderType}'.")
    };

    private static OrderStatus MapOrderStatus(string status) => status switch
    {
        "New" => OrderStatus.Accepted,
        "PartiallyFilled" => OrderStatus.PartiallyFilled,
        "Filled" => OrderStatus.Filled,
        "Cancelled" => OrderStatus.Cancelled,
        "Canceled" => OrderStatus.Cancelled,
        "PartiallyFilledCanceled" => OrderStatus.Cancelled,
        "PartiallyFilledCancelled" => OrderStatus.Cancelled,
        "Rejected" => OrderStatus.Rejected,
        "Deactivated" => OrderStatus.Cancelled,
        _ => OrderStatus.Unknown
    };

    private static decimal ParseDecimal(string? value)
    {
        return decimal.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0m;
    }

    private static decimal? ParseNullableDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return decimal.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    private static DateTimeOffset ParseUnixMilliseconds(string? value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : DateTimeOffset.UtcNow;
    }

    private static string FormatDecimal(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string BuildQuery(params (string Key, string? Value)[] parameters)
    {
        return string.Join(
            "&",
            parameters
                .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Value))
                .Select(parameter =>
                    $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value!)}"));
    }

    private Uri BuildUri(string path, string? query = null)
    {
        var baseUri = new Uri(_options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var relative = path.TrimStart('/');

        if (!string.IsNullOrEmpty(query))
        {
            relative += "?" + query;
        }

        return new Uri(baseUri, relative);
    }

    private void ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException("Exchange:Bybit:ApiKey is not configured.");
        }

        if (string.IsNullOrWhiteSpace(_options.ApiSecret))
        {
            throw new InvalidOperationException("Exchange:Bybit:ApiSecret is not configured.");
        }
    }

    private static void ValidateTestnetBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "api-testnet.bybit.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "TradeOps v1.1.1.2 permits only the Bybit testnet endpoint https://api-testnet.bybit.com.");
        }
    }
}
