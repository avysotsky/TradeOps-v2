using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Binance;

public sealed class BinanceFuturesExchangeClient : IExchangeClient, IExchangeConnectionManager
{
    public const string HttpClientName = "binance-futures";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> AllowedTestnetHosts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "testnet.binancefuture.com",
            "demo-fapi.binance.com"
        };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BinanceOptions _options;
    private readonly ILogger<BinanceFuturesExchangeClient> _logger;
    private volatile bool _isConnected;

    public BinanceFuturesExchangeClient(
        IHttpClientFactory httpClientFactory,
        BinanceOptions options,
        ILogger<BinanceFuturesExchangeClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        ValidateTestnetBaseUrl(options.BaseUrl);
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
                "ExchangeAuthenticationSucceeded for Binance USD-M Futures testnet.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _isConnected = false;
            _logger.LogError(
                exception,
                "ExchangeAuthenticationFailed for Binance USD-M Futures testnet.");
            throw;
        }
    }

    public async Task<AccountInfo> GetAccountAsync(
        CancellationToken cancellationToken = default)
    {
        var account = await SendPrivateAsync<BinanceAccountDto>(
            HttpMethod.Get,
            "/fapi/v3/account",
            [],
            cancellationToken);

        _isConnected = true;

        return new AccountInfo
        {
            Currency = _options.SettlementCurrency.Trim().ToUpperInvariant(),
            Balance = ParseDecimal(account.TotalWalletBalance),
            Equity = ParseDecimal(account.TotalMarginBalance),
            AvailableBalance = ParseDecimal(account.AvailableBalance)
        };
    }

    public async Task<IReadOnlyCollection<Position>> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        var positions = await SendPrivateAsync<BinancePositionDto[]>(
            HttpMethod.Get,
            "/fapi/v3/positionRisk",
            [],
            cancellationToken);

        return positions
            .Select(MapPosition)
            .Where(position => position.Quantity > 0m)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(
        CancellationToken cancellationToken = default)
    {
        var orders = await SendPrivateAsync<BinanceOrderDto[]>(
            HttpMethod.Get,
            "/fapi/v1/openOrders",
            [],
            cancellationToken);

        return orders.Select(MapOrder).ToArray();
    }

    public async Task<OrderResult> PlaceOrderAsync(
        PlaceOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.ClientOrderId.Length > 36)
        {
            throw new ArgumentException(
                "Binance newClientOrderId cannot exceed 36 characters.",
                nameof(request));
        }

        if (request.OrderType == OrderType.Limit && request.Price is null)
        {
            return RejectLocally(request, "Limit orders require Price.");
        }

        var parameters = new List<KeyValuePair<string, string?>>
        {
            Pair("symbol", NormalizeSymbol(request.Symbol)),
            Pair("side", request.Side == OrderSide.Buy ? "BUY" : "SELL"),
            Pair("type", request.OrderType switch
            {
                OrderType.Market => "MARKET",
                OrderType.Limit => "LIMIT",
                _ => throw new NotSupportedException(
                    $"Binance adapter does not support TradeOps order type {request.OrderType} yet.")
            }),
            Pair("quantity", FormatDecimal(request.Quantity)),
            Pair("newClientOrderId", request.ClientOrderId),
            Pair("newOrderRespType", "RESULT")
        };

        if (request.OrderType == OrderType.Limit)
        {
            parameters.Add(Pair("timeInForce", "GTC"));
            parameters.Add(Pair("price", FormatDecimal(request.Price!.Value)));
        }

        _logger.LogInformation(
            "OrderPlacementRequested for {ClientOrderId}: {Side} {Quantity} {Symbol} on Binance Futures testnet.",
            request.ClientOrderId,
            request.Side,
            request.Quantity,
            request.Symbol);

        try
        {
            var order = await SendPrivateAsync<BinanceOrderDto>(
                HttpMethod.Post,
                "/fapi/v1/order",
                parameters,
                cancellationToken);

            var mapped = MapOrder(order);

            _logger.LogInformation(
                "OrderPlacementConfirmed for {ClientOrderId}; Binance order {ExchangeOrderId} returned status {Status}.",
                request.ClientOrderId,
                mapped.ExchangeOrderId,
                mapped.Status);

            return ToOrderResult(mapped);
        }
        catch (BinanceApiException exception) when (exception.Code == -4116)
        {
            var existing = await GetOrderByClientOrderIdAsync(
                request.ClientOrderId,
                request.Symbol,
                cancellationToken);

            if (existing is not null)
            {
                return ToOrderResult(existing);
            }

            throw;
        }
        catch (BinanceApiException exception)
        {
            _logger.LogWarning(
                exception,
                "Binance explicitly rejected order {ClientOrderId}.",
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
            _logger.LogWarning(
                exception,
                "OrderPlacementAmbiguous for {ClientOrderId} because the Binance request timed out.",
                request.ClientOrderId);

            throw new TimeoutException(
                $"Binance order placement timed out for {request.ClientOrderId}.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "OrderPlacementAmbiguous for {ClientOrderId} because the Binance HTTP outcome is unknown.",
                request.ClientOrderId);

            throw new TimeoutException(
                $"Binance order placement outcome is ambiguous for {request.ClientOrderId}.",
                exception);
        }
    }

    public Task CancelOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default) =>
        CancelOrderAsync(exchangeOrderId, null, cancellationToken);

    public async Task CancelOrderAsync(
        string exchangeOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = RequireSymbol(symbol);

        _ = await SendPrivateAsync<BinanceOrderDto>(
            HttpMethod.Delete,
            "/fapi/v1/order",
            [
                Pair("symbol", normalizedSymbol),
                Pair("orderId", exchangeOrderId)
            ],
            cancellationToken);

        _logger.LogInformation(
            "OrderCancellationAcknowledged for Binance order {ExchangeOrderId} on {Symbol}.",
            exchangeOrderId,
            normalizedSymbol);
    }

    public Task<Order?> GetOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default) =>
        GetOrderAsync(exchangeOrderId, null, cancellationToken);

    public async Task<Order?> GetOrderAsync(
        string exchangeOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var order = await SendPrivateAsync<BinanceOrderDto>(
                HttpMethod.Get,
                "/fapi/v1/order",
                [
                    Pair("symbol", RequireSymbol(symbol)),
                    Pair("orderId", exchangeOrderId)
                ],
                cancellationToken);

            return MapOrder(order);
        }
        catch (BinanceApiException exception) when (exception.Code == -2013)
        {
            return null;
        }
    }

    public Task<Order?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default) =>
        GetOrderByClientOrderIdAsync(clientOrderId, null, cancellationToken);

    public async Task<Order?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var order = await SendPrivateAsync<BinanceOrderDto>(
                HttpMethod.Get,
                "/fapi/v1/order",
                [
                    Pair("symbol", RequireSymbol(symbol)),
                    Pair("origClientOrderId", clientOrderId)
                ],
                cancellationToken);

            return MapOrder(order);
        }
        catch (BinanceApiException exception) when (exception.Code == -2013)
        {
            return null;
        }
    }

    private async Task<T> SendPrivateAsync<T>(
        HttpMethod method,
        string path,
        IEnumerable<KeyValuePair<string, string?>> parameters,
        CancellationToken cancellationToken)
    {
        ValidateCredentials();

        var signedParameters = parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Value))
            .ToList();

        signedParameters.Add(Pair(
            "recvWindow",
            _options.RecvWindowMilliseconds.ToString(CultureInfo.InvariantCulture)));
        signedParameters.Add(Pair(
            "timestamp",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(
                CultureInfo.InvariantCulture)));

        var query = BuildQuery(signedParameters);
        var signature = BinanceSigner.CreateHmacSha256Hex(
            _options.ApiSecret,
            query);
        var signedQuery = $"{query}&signature={signature}";

        using var request = new HttpRequestMessage(
            method,
            BuildUri(path, signedQuery));
        request.Headers.TryAddWithoutValidation(
            "X-MBX-APIKEY",
            _options.ApiKey);

        return await SendAsync<T>(request, cancellationToken);
    }

    private async Task<T> SendAsync<T>(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
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

                BinanceErrorDto? error = null;
                try
                {
                    error = JsonSerializer.Deserialize<BinanceErrorDto>(
                        json,
                        JsonOptions);
                }
                catch (JsonException)
                {
                }

                if (error is not null)
                {
                    throw new BinanceApiException(
                        error.Code,
                        string.IsNullOrWhiteSpace(error.Message)
                            ? response.ReasonPhrase ?? "Unknown Binance error."
                            : error.Message);
                }

                throw new HttpRequestException(
                    $"Binance returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }

            var result = JsonSerializer.Deserialize<T>(json, JsonOptions)
                ?? throw new InvalidOperationException(
                    "Binance returned an empty or invalid JSON response.");

            _isConnected = true;
            return result;
        }
        catch (HttpRequestException)
        {
            _isConnected = false;
            throw;
        }
    }

    private void ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)
            || string.IsNullOrWhiteSpace(_options.ApiSecret))
        {
            throw new InvalidOperationException(
                "Binance Futures testnet API credentials are not configured.");
        }
    }

    private static void ValidateTestnetBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !AllowedTestnetHosts.Contains(uri.Host))
        {
            throw new InvalidOperationException(
                "Binance adapter is restricted to approved non-production Futures testnet/demo hosts.");
        }
    }

    private static string RequireSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException(
                "Binance order lookup/cancellation requires a symbol.",
                nameof(symbol));
        }

        return NormalizeSymbol(symbol);
    }

    private static string NormalizeSymbol(string symbol) =>
        symbol.Trim().ToUpperInvariant();

    private static KeyValuePair<string, string?> Pair(
        string key,
        string? value) =>
        new(key, value);

    private static string BuildQuery(
        IEnumerable<KeyValuePair<string, string?>> parameters) =>
        string.Join(
            "&",
            parameters.Select(parameter =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value ?? string.Empty)}"));

    private string BuildUri(string path, string query) =>
        $"{_options.BaseUrl.TrimEnd('/')}{path}?{query}";

    private static Position MapPosition(BinancePositionDto position)
    {
        var signedQuantity = ParseDecimal(position.PositionAmount);

        return new Position
        {
            Symbol = position.Symbol,
            Side = signedQuantity >= 0m ? OrderSide.Buy : OrderSide.Sell,
            Quantity = Math.Abs(signedQuantity),
            AverageEntryPrice = ParseDecimal(position.EntryPrice),
            MarkPrice = ParseDecimal(position.MarkPrice),
            UnrealizedPnL = ParseDecimal(position.UnrealizedProfit)
        };
    }

    private static Order MapOrder(BinanceOrderDto order)
    {
        var createdAtMilliseconds = order.Time > 0
            ? order.Time
            : order.UpdateTime;
        var updatedAtMilliseconds = order.UpdateTime > 0
            ? order.UpdateTime
            : createdAtMilliseconds;

        return new Order
        {
            Id = Guid.NewGuid(),
            ExchangeOrderId = order.OrderId.ToString(CultureInfo.InvariantCulture),
            ClientOrderId = order.ClientOrderId,
            Symbol = order.Symbol,
            Side = MapSide(order.Side),
            OrderType = MapOrderType(order.Type),
            RequestedQuantity = ParseDecimal(order.OriginalQuantity),
            FilledQuantity = ParseDecimal(order.ExecutedQuantity),
            AverageFillPrice = ParsePositiveNullableDecimal(order.AveragePrice),
            Price = ParsePositiveNullableDecimal(order.Price),
            Status = MapOrderStatus(order.Status),
            CreatedAt = ParseUnixMilliseconds(createdAtMilliseconds),
            UpdatedAt = ParseUnixMilliseconds(updatedAtMilliseconds)
        };
    }

    private static OrderResult ToOrderResult(Order order) =>
        new(
            order.ExchangeOrderId,
            order.ClientOrderId,
            order.Status,
            order.FilledQuantity,
            order.AverageFillPrice);

    private static OrderResult RejectLocally(
        PlaceOrderRequest request,
        string reason)
    {
        _ = reason;
        return new OrderResult(
            null,
            request.ClientOrderId,
            OrderStatus.Rejected,
            0m,
            null);
    }

    private static OrderSide MapSide(string side) =>
        side.ToUpperInvariant() switch
        {
            "BUY" => OrderSide.Buy,
            "SELL" => OrderSide.Sell,
            _ => throw new InvalidOperationException(
                $"Unsupported Binance side '{side}'.")
        };

    private static OrderType MapOrderType(string orderType) =>
        orderType.ToUpperInvariant() switch
        {
            "MARKET" => OrderType.Market,
            "LIMIT" => OrderType.Limit,
            _ => throw new InvalidOperationException(
                $"Unsupported Binance order type '{orderType}'.")
        };

    private static OrderStatus MapOrderStatus(string status) =>
        status.ToUpperInvariant() switch
        {
            "NEW" => OrderStatus.Accepted,
            "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
            "FILLED" => OrderStatus.Filled,
            "CANCELED" => OrderStatus.Cancelled,
            "CANCELLED" => OrderStatus.Cancelled,
            "REJECTED" => OrderStatus.Rejected,
            "EXPIRED" => OrderStatus.Cancelled,
            "EXPIRED_IN_MATCH" => OrderStatus.Cancelled,
            _ => OrderStatus.Unknown
        };

    private static decimal ParseDecimal(string? value) =>
        decimal.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0m;

    private static decimal? ParsePositiveNullableDecimal(string? value)
    {
        var parsed = ParseDecimal(value);
        return parsed > 0m ? parsed : null;
    }

    private static DateTimeOffset ParseUnixMilliseconds(long milliseconds) =>
        milliseconds > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : DateTimeOffset.UtcNow;

    private static string FormatDecimal(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
