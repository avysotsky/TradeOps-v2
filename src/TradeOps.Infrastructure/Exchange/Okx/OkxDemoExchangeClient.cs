using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Okx;

public sealed class OkxDemoExchangeClient
    : IExchangeClient, IExchangeConnectionManager
{
    public const string HttpClientName = "okx-demo";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OkxOptions _options;
    private readonly ILogger<OkxDemoExchangeClient> _logger;

    private volatile bool _isConnected;

    public OkxDemoExchangeClient(
        IHttpClientFactory httpClientFactory,
        OkxOptions options,
        ILogger<OkxDemoExchangeClient> logger)
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
                "ExchangeReadinessSucceeded for OKX Demo.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _isConnected = false;
            _logger.LogError(
                exception,
                "ExchangeReadinessFailed for OKX Demo.");
            throw;
        }
    }

    public async Task<AccountInfo> GetAccountAsync(
        CancellationToken cancellationToken = default)
    {
        var currency = _options.AccountCurrency
            .Trim()
            .ToUpperInvariant();

        var data = await SendPrivateAsync(
            HttpMethod.Get,
            "/api/v5/account/balance",
            [
                Pair("ccy", currency)
            ],
            body: null,
            cancellationToken);

        var account = First(data)
            ?? throw new InvalidOperationException(
                "OKX balance response did not contain account data.");

        if (!account.TryGetProperty(
                "details",
                out var details)
            || details.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "OKX balance response did not contain currency details.");
        }

        var detail = details
            .EnumerateArray()
            .FirstOrDefault(item =>
                string.Equals(
                    ReadString(item, "ccy"),
                    currency,
                    StringComparison.OrdinalIgnoreCase));

        if (detail.ValueKind == JsonValueKind.Undefined)
        {
            return new AccountInfo
            {
                Currency = currency,
                Balance = 0m,
                Equity = 0m,
                AvailableBalance = 0m
            };
        }

        var available =
            ReadDecimal(detail, "availEq");

        if (available == 0m)
        {
            available = ReadDecimal(
                detail,
                "availBal");
        }

        return new AccountInfo
        {
            Currency = currency,
            Balance = ReadDecimal(
                detail,
                "cashBal"),
            Equity = ReadDecimal(
                detail,
                "eq"),
            AvailableBalance = available
        };
    }

    public async Task<IReadOnlyCollection<Position>> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        var data = await SendPrivateAsync(
            HttpMethod.Get,
            "/api/v5/account/positions",
            [],
            body: null,
            cancellationToken);

        if (data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Position>();
        }

        return data
            .EnumerateArray()
            .Where(item =>
                Math.Abs(ReadDecimal(item, "pos")) > 0m)
            .Select(MapPosition)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(
        CancellationToken cancellationToken = default)
    {
        var data = await SendPrivateAsync(
            HttpMethod.Get,
            "/api/v5/trade/orders-pending",
            [],
            body: null,
            cancellationToken);

        if (data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Order>();
        }

        return data
            .EnumerateArray()
            .Where(item => IsDerivativeInstrument(
                ReadString(item, "instId")))
            .Select(MapOrder)
            .ToArray();
    }

    public async Task<OrderResult> PlaceOrderAsync(
        PlaceOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateDerivativeInstrument(request.Symbol);

        if (request.Quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "OKX order size must be positive.");
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
                $"OKX Demo adapter does not support TradeOps order type {request.OrderType}.");
        }

        var clOrdId = OkxClientOrderIdFactory.Create(
            request.ClientOrderId);

        var payload =
            new Dictionary<string, string>
            {
                ["instId"] =
                    request.Symbol.Trim().ToUpperInvariant(),
                ["tdMode"] = NormalizeTradeMode(),
                ["clOrdId"] = clOrdId,
                ["side"] =
                    request.Side == OrderSide.Buy
                        ? "buy"
                        : "sell",
                ["ordType"] =
                    request.OrderType == OrderType.Market
                        ? "market"
                        : "limit",
                ["sz"] = FormatDecimal(request.Quantity)
            };

        if (request.Price is not null)
        {
            payload["px"] =
                FormatDecimal(request.Price.Value);
        }

        _logger.LogInformation(
            "OrderPlacementRequested for {ClientOrderId}: {Side} {Size} {Instrument} on OKX Demo.",
            request.ClientOrderId,
            request.Side,
            request.Quantity,
            request.Symbol);

        try
        {
            var data = await SendPrivateAsync(
                HttpMethod.Post,
                "/api/v5/trade/order",
                [],
                payload,
                cancellationToken);

            var ack = First(data)
                ?? throw new InvalidOperationException(
                    "OKX place-order response did not contain acknowledgement.");

            EnsureItemSucceeded(
                ack,
                "place order");

            var orderId = ReadString(
                ack,
                "ordId");

            return new OrderResult(
                orderId,
                request.ClientOrderId,
                OrderStatus.Accepted,
                0m,
                null);
        }
        catch (OkxApiException exception)
        {
            _logger.LogWarning(
                exception,
                "OKX Demo explicitly rejected order {ClientOrderId}.",
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
                $"OKX Demo order placement timed out for {request.ClientOrderId}.",
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
                $"OKX Demo order placement outcome is ambiguous for {request.ClientOrderId}.",
                exception);
        }
    }

    public async Task CancelOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetOrderAsync(
            exchangeOrderId,
            cancellationToken)
            ?? throw new KeyNotFoundException(
                $"OKX Demo order '{exchangeOrderId}' was not found.");

        await CancelOrderAsync(
            exchangeOrderId,
            existing.Symbol,
            cancellationToken);
    }

    public async Task CancelOrderAsync(
        string exchangeOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            var existing = await GetOrderAsync(
                exchangeOrderId,
                cancellationToken);

            symbol = existing?.Symbol
                ?? throw new InvalidOperationException(
                    "OKX cancellation requires a resolvable instrument.");
        }

        ValidateDerivativeInstrument(symbol);

        var data = await SendPrivateAsync(
            HttpMethod.Post,
            "/api/v5/trade/cancel-order",
            [],
            new Dictionary<string, string>
            {
                ["instId"] =
                    symbol.Trim().ToUpperInvariant(),
                ["ordId"] = exchangeOrderId
            },
            cancellationToken);

        var ack = First(data)
            ?? throw new InvalidOperationException(
                "OKX cancel response did not contain acknowledgement.");

        EnsureItemSucceeded(
            ack,
            "cancel order");
    }

    public async Task<Order?> GetOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(exchangeOrderId))
        {
            return null;
        }

        var scanned = await FindOrderInListsAsync(
            item => string.Equals(
                ReadString(item, "ordId"),
                exchangeOrderId,
                StringComparison.Ordinal),
            cancellationToken);

        return scanned;
    }

    public async Task<Order?> GetOrderAsync(
        string exchangeOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return await GetOrderAsync(
                exchangeOrderId,
                cancellationToken);
        }

        return await GetOrderDirectAsync(
            symbol,
            "ordId",
            exchangeOrderId,
            cancellationToken);
    }

    public Task<Order?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        var clOrdId =
            OkxClientOrderIdFactory.Create(clientOrderId);

        return FindOrderInListsAsync(
            item => string.Equals(
                ReadString(item, "clOrdId"),
                clOrdId,
                StringComparison.Ordinal),
            cancellationToken);
    }

    public async Task<Order?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return await GetOrderByClientOrderIdAsync(
                clientOrderId,
                cancellationToken);
        }

        return await GetOrderDirectAsync(
            symbol,
            "clOrdId",
            OkxClientOrderIdFactory.Create(clientOrderId),
            cancellationToken);
    }

    internal static void ValidateDerivativeInstrument(
        string symbol)
    {
        if (!IsDerivativeInstrument(symbol))
        {
            throw new ArgumentException(
                "OKX Demo adapter requires a native derivatives instId such as BTC-USDT-SWAP or a dated futures instrument.",
                nameof(symbol));
        }
    }

    internal static bool IsDerivativeInstrument(
        string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return false;
        }

        return symbol
            .Split(
                '-',
                StringSplitOptions.RemoveEmptyEntries)
            .Length >= 3;
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
                    request.Symbol,
                    cancellationToken);

            return existing is null
                ? null
                : ToOrderResult(
                    existing,
                    request.ClientOrderId);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "OKX Demo ambiguous placement recovery failed for {ClientOrderId}.",
                request.ClientOrderId);

            return null;
        }
    }

    private async Task<Order?> GetOrderDirectAsync(
        string symbol,
        string lookupKey,
        string lookupValue,
        CancellationToken cancellationToken)
    {
        ValidateDerivativeInstrument(symbol);

        try
        {
            var data = await SendPrivateAsync(
                HttpMethod.Get,
                "/api/v5/trade/order",
                [
                    Pair(
                        "instId",
                        symbol.Trim().ToUpperInvariant()),
                    Pair(
                        lookupKey,
                        lookupValue)
                ],
                body: null,
                cancellationToken);

            var item = First(data);

            return item is null
                ? null
                : MapOrder(item.Value);
        }
        catch (OkxApiException exception)
            when (exception.Code is "51603" or "51604")
        {
            return null;
        }
    }

    private async Task<Order?> FindOrderInListsAsync(
        Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        var pending = await SendPrivateAsync(
            HttpMethod.Get,
            "/api/v5/trade/orders-pending",
            [],
            body: null,
            cancellationToken);

        if (pending.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in pending.EnumerateArray())
            {
                if (predicate(item))
                {
                    return MapOrder(item);
                }
            }
        }

        foreach (var instType in new[] { "SWAP", "FUTURES" })
        {
            var history = await SendPrivateAsync(
                HttpMethod.Get,
                "/api/v5/trade/orders-history",
                [
                    Pair("instType", instType),
                    Pair("limit", "100")
                ],
                body: null,
                cancellationToken);

            if (history.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in history.EnumerateArray())
            {
                if (predicate(item))
                {
                    return MapOrder(item);
                }
            }
        }

        return null;
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
        var requestPath =
            string.IsNullOrWhiteSpace(query)
                ? path
                : $"{path}?{query}";

        var bodyJson =
            body is null
                ? string.Empty
                : JsonSerializer.Serialize(
                    body,
                    JsonOptions);

        var timestamp =
            DateTimeOffset.UtcNow
                .ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                    CultureInfo.InvariantCulture);

        var signature = OkxSigner.Sign(
            timestamp,
            method.Method,
            requestPath,
            bodyJson,
            _options.ApiSecret);

        using var request = new HttpRequestMessage(
            method,
            $"{_options.BaseUrl.TrimEnd('/')}{requestPath}");

        request.Headers.TryAddWithoutValidation(
            "OK-ACCESS-KEY",
            _options.ApiKey);
        request.Headers.TryAddWithoutValidation(
            "OK-ACCESS-SIGN",
            signature);
        request.Headers.TryAddWithoutValidation(
            "OK-ACCESS-TIMESTAMP",
            timestamp);
        request.Headers.TryAddWithoutValidation(
            "OK-ACCESS-PASSPHRASE",
            _options.Passphrase);

        // Hard safety boundary: this provider always uses demo trading.
        request.Headers.TryAddWithoutValidation(
            "x-simulated-trading",
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
                    $"OKX Demo returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var code = ReadString(root, "code");

            if (!string.Equals(
                    code,
                    "0",
                    StringComparison.Ordinal))
            {
                throw new OkxApiException(
                    code,
                    ReadString(root, "msg"));
            }

            if (!root.TryGetProperty(
                    "data",
                    out var data))
            {
                throw new InvalidOperationException(
                    "OKX Demo response did not contain data.");
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

    private static void EnsureItemSucceeded(
        JsonElement item,
        string operation)
    {
        var code = ReadString(item, "sCode");

        if (!string.IsNullOrWhiteSpace(code)
            && !string.Equals(
                code,
                "0",
                StringComparison.Ordinal))
        {
            throw new OkxApiException(
                code,
                $"{operation}: {ReadString(item, "sMsg")}");
        }
    }

    private static Position MapPosition(
        JsonElement item)
    {
        var rawPosition = ReadDecimal(
            item,
            "pos");

        var posSide = ReadString(
            item,
            "posSide");

        var side =
            posSide.Equals(
                "short",
                StringComparison.OrdinalIgnoreCase)
            || (posSide.Equals(
                    "net",
                    StringComparison.OrdinalIgnoreCase)
                && rawPosition < 0m)
                ? OrderSide.Sell
                : OrderSide.Buy;

        return new Position
        {
            Symbol = ReadString(item, "instId"),
            Side = side,
            Quantity = Math.Abs(rawPosition),
            AverageEntryPrice =
                ReadDecimal(item, "avgPx"),
            MarkPrice =
                ReadDecimal(item, "markPx"),
            UnrealizedPnL =
                ReadDecimal(item, "upl")
        };
    }

    private static Order MapOrder(
        JsonElement item)
    {
        var requested =
            ReadDecimal(item, "sz");
        var filled =
            Math.Min(
                requested,
                ReadDecimal(item, "accFillSz"));

        var state =
            ReadString(item, "state");

        if (state.Equals(
                "live",
                StringComparison.OrdinalIgnoreCase)
            && filled > 0m
            && filled < requested)
        {
            state = "partially_filled";
        }

        var createdAt =
            ReadTimestamp(item, "cTime");
        var updatedAt =
            ReadTimestamp(
                item,
                "uTime",
                createdAt);

        return new Order
        {
            Id = Guid.NewGuid(),
            ExchangeOrderId =
                ReadString(item, "ordId"),
            ClientOrderId =
                string.IsNullOrWhiteSpace(
                    ReadString(item, "clOrdId"))
                    ? $"okx-{ReadString(item, "ordId")}"
                    : ReadString(item, "clOrdId"),
            Symbol =
                ReadString(item, "instId"),
            Side =
                ReadString(item, "side")
                    .Equals(
                        "sell",
                        StringComparison.OrdinalIgnoreCase)
                    ? OrderSide.Sell
                    : OrderSide.Buy,
            OrderType =
                ReadString(item, "ordType")
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
                    "avgPx"),
            Price =
                ReadPositiveNullableDecimal(
                    item,
                    "px"),
            Status =
                MapOrderStatus(state),
            CreatedAt = createdAt,
            UpdatedAt = updatedAt
        };
    }

    private static OrderStatus MapOrderStatus(
        string state) =>
        state.ToLowerInvariant() switch
        {
            "live" => OrderStatus.Accepted,
            "partially_filled" =>
                OrderStatus.PartiallyFilled,
            "filled" => OrderStatus.Filled,
            "canceled" => OrderStatus.Cancelled,
            "cancelled" => OrderStatus.Cancelled,
            "mmp_canceled" => OrderStatus.Cancelled,
            "order_failed" => OrderStatus.Rejected,
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
                "openapi.okx.com",
                StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Trim('/') != string.Empty)
        {
            throw new InvalidOperationException(
                "OKX Demo adapter is restricted to the official openapi.okx.com REST host.");
        }

        var tradeMode =
            NormalizeTradeMode();

        if (tradeMode is not ("cross" or "isolated"))
        {
            throw new InvalidOperationException(
                "OKX Demo TradeMode must be 'cross' or 'isolated'.");
        }
    }

    private void ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)
            || string.IsNullOrWhiteSpace(_options.ApiSecret)
            || string.IsNullOrWhiteSpace(_options.Passphrase))
        {
            throw new InvalidOperationException(
                "OKX Demo ApiKey, ApiSecret and Passphrase are required.");
        }
    }

    private string NormalizeTradeMode() =>
        _options.TradeMode
            .Trim()
            .ToLowerInvariant();

    private static string BuildQuery(
        IEnumerable<KeyValuePair<string, string?>> parameters) =>
        string.Join(
            "&",
            parameters
                .Where(item => item.Value is not null)
                .Select(item =>
                    $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value ?? string.Empty)}"));

    private static OrderResult ToOrderResult(
        Order order,
        string originalClientOrderId) =>
        new(
            order.ExchangeOrderId,
            originalClientOrderId,
            order.Status,
            order.FilledQuantity,
            order.AverageFillPrice);

    private static JsonElement? First(
        JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var enumerator =
            data.EnumerateArray();

        return enumerator.MoveNext()
            ? enumerator.Current
            : null;
    }

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
        var raw = ReadString(element, name);

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
