using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.KuCoin;

public sealed class KuCoinFuturesReadOnlyExchangeClient
    : IExchangeClient, IExchangeConnectionManager
{
    public const string HttpClientName = "kucoin-futures-readonly";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly KuCoinFuturesOptions _options;
    private readonly ILogger<KuCoinFuturesReadOnlyExchangeClient> _logger;
    private volatile bool _isConnected;

    public KuCoinFuturesReadOnlyExchangeClient(
        IHttpClientFactory httpClientFactory,
        KuCoinFuturesOptions options,
        ILogger<KuCoinFuturesReadOnlyExchangeClient> logger)
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
                "ExchangeReadinessSucceeded for KuCoin Futures read-only.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _isConnected = false;
            _logger.LogError(
                exception,
                "ExchangeReadinessFailed for KuCoin Futures read-only.");
            throw;
        }
    }

    public async Task<AccountInfo> GetAccountAsync(
        CancellationToken cancellationToken = default)
    {
        var currency = _options.AccountCurrency
            .Trim()
            .ToUpperInvariant();

        var data = await SendPrivateGetAsync(
            "/api/v1/account-overview",
            [
                Pair("currency", currency)
            ],
            cancellationToken);

        return new AccountInfo
        {
            Currency = ReadString(data, "currency"),
            Balance = ReadDecimal(data, "marginBalance"),
            Equity = ReadDecimal(data, "accountEquity"),
            AvailableBalance = ReadDecimal(data, "availableBalance")
        };
    }

    public async Task<IReadOnlyCollection<Position>> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        var data = await SendPrivateGetAsync(
            "/api/v1/positions",
            [
                Pair(
                    "currency",
                    _options.AccountCurrency.Trim().ToUpperInvariant())
            ],
            cancellationToken);

        if (data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Position>();
        }

        return data
            .EnumerateArray()
            .Where(item =>
                ReadBoolean(item, "isOpen")
                && ReadDecimal(item, "currentQty") != 0m)
            .Select(MapPosition)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(
        CancellationToken cancellationToken = default)
    {
        var data = await SendPrivateGetAsync(
            "/api/v1/orders",
            [
                Pair("status", "active"),
                Pair("pageSize", "100")
            ],
            cancellationToken);

        if (!data.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Order>();
        }

        return items
            .EnumerateArray()
            .Select(MapOrder)
            .ToArray();
    }

    public Task<OrderResult> PlaceOrderAsync(
        PlaceOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = request;
        _ = cancellationToken;
        throw ReadOnlyException();
    }

    public Task CancelOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default)
    {
        _ = exchangeOrderId;
        _ = cancellationToken;
        throw ReadOnlyException();
    }

    public Task CancelOrderAsync(
        string exchangeOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        _ = exchangeOrderId;
        _ = symbol;
        _ = cancellationToken;
        throw ReadOnlyException();
    }

    public async Task<Order?> GetOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(exchangeOrderId))
        {
            return null;
        }

        try
        {
            var data = await SendPrivateGetAsync(
                $"/api/v1/orders/{Uri.EscapeDataString(exchangeOrderId)}",
                [],
                cancellationToken);

            return MapOrder(data);
        }
        catch (KuCoinApiException exception)
            when (exception.Code is "404000" or "200004")
        {
            return null;
        }
    }

    public Task<Order?> GetOrderAsync(
        string exchangeOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        _ = symbol;
        return GetOrderAsync(exchangeOrderId, cancellationToken);
    }

    public async Task<Order?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientOrderId))
        {
            return null;
        }

        try
        {
            var data = await SendPrivateGetAsync(
                "/api/v1/orders/byClientOid",
                [
                    Pair("clientOid", clientOrderId)
                ],
                cancellationToken);

            return MapOrder(data);
        }
        catch (KuCoinApiException exception)
            when (exception.Code is "404000" or "200004")
        {
            return null;
        }
    }

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

    private async Task<JsonElement> SendPrivateGetAsync(
        string endpoint,
        IEnumerable<KeyValuePair<string, string?>> parameters,
        CancellationToken cancellationToken)
    {
        ValidateCredentials();

        var rawQuery = string.Join(
            "&",
            parameters
                .Where(item => item.Value is not null)
                .Select(item =>
                    $"{item.Key}={item.Value}"));

        var encodedQuery = string.Join(
            "&",
            parameters
                .Where(item => item.Value is not null)
                .Select(item =>
                    $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value ?? string.Empty)}"));

        var signingEndpoint = string.IsNullOrWhiteSpace(rawQuery)
            ? endpoint
            : $"{endpoint}?{rawQuery}";

        var requestEndpoint = string.IsNullOrWhiteSpace(encodedQuery)
            ? endpoint
            : $"{endpoint}?{encodedQuery}";

        var timestamp = DateTimeOffset.UtcNow
            .ToUnixTimeMilliseconds()
            .ToString(CultureInfo.InvariantCulture);

        var signature = KuCoinSigner.Sign(
            timestamp,
            "GET",
            signingEndpoint,
            string.Empty,
            _options.ApiSecret);

        var encryptedPassphrase =
            KuCoinSigner.EncryptPassphrase(
                _options.Passphrase,
                _options.ApiSecret);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            _options.BaseUrl.TrimEnd('/') + requestEndpoint);

        request.Headers.TryAddWithoutValidation(
            "KC-API-KEY",
            _options.ApiKey);
        request.Headers.TryAddWithoutValidation(
            "KC-API-SIGN",
            signature);
        request.Headers.TryAddWithoutValidation(
            "KC-API-TIMESTAMP",
            timestamp);
        request.Headers.TryAddWithoutValidation(
            "KC-API-PASSPHRASE",
            encryptedPassphrase);
        request.Headers.TryAddWithoutValidation(
            "KC-API-KEY-VERSION",
            _options.KeyVersion);

        var client = _httpClientFactory.CreateClient(HttpClientName);

        try
        {
            using var response = await client.SendAsync(
                request,
                cancellationToken);

            var json = await response.Content.ReadAsStringAsync(
                cancellationToken);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var code = ReadString(root, "code");

            if (!response.IsSuccessStatusCode
                || !string.Equals(
                    code,
                    "200000",
                    StringComparison.Ordinal))
            {
                if ((int)response.StatusCode >= 500)
                {
                    _isConnected = false;
                }

                throw new KuCoinApiException(
                    code,
                    ReadString(root, "msg"));
            }

            if (!root.TryGetProperty("data", out var data))
            {
                throw new InvalidOperationException(
                    "KuCoin Futures response did not contain data.");
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

    private static Position MapPosition(JsonElement item)
    {
        var currentQty = ReadDecimal(item, "currentQty");
        var side = ReadString(item, "positionSide");

        return new Position
        {
            Symbol = ReadString(item, "symbol"),
            Side = side.Equals(
                    "SHORT",
                    StringComparison.OrdinalIgnoreCase)
                || currentQty < 0m
                ? OrderSide.Sell
                : OrderSide.Buy,
            Quantity = Math.Abs(currentQty),
            AverageEntryPrice = ReadDecimal(item, "avgEntryPrice"),
            MarkPrice = ReadDecimal(item, "markPrice"),
            UnrealizedPnL = ReadDecimal(item, "unrealisedPnl")
        };
    }

    private static Order MapOrder(JsonElement item)
    {
        var requested = Math.Abs(ReadDecimal(item, "size"));
        var filled = Math.Min(
            requested,
            Math.Abs(
                ReadDecimal(item, "filledSize") != 0m
                    ? ReadDecimal(item, "filledSize")
                    : ReadDecimal(item, "dealSize")));

        var orderId = ReadString(item, "id");
        var clientOrderId = ReadString(item, "clientOid");

        var status = ReadString(item, "status");
        var isActive = ReadBoolean(item, "isActive");

        return new Order
        {
            Id = Guid.NewGuid(),
            ExchangeOrderId = orderId,
            ClientOrderId = string.IsNullOrWhiteSpace(clientOrderId)
                ? $"kucoin-{orderId}"
                : clientOrderId,
            Symbol = ReadString(item, "symbol"),
            Side = ReadString(item, "side")
                .Equals("sell", StringComparison.OrdinalIgnoreCase)
                ? OrderSide.Sell
                : OrderSide.Buy,
            OrderType = ReadString(item, "type")
                .Equals("market", StringComparison.OrdinalIgnoreCase)
                ? OrderType.Market
                : OrderType.Limit,
            RequestedQuantity = requested,
            FilledQuantity = filled,
            AverageFillPrice = ReadPositiveDecimal(
                item,
                "avgDealPrice"),
            Price = ReadPositiveDecimal(item, "price"),
            Status = MapOrderStatus(
                status,
                isActive,
                filled,
                requested),
            CreatedAt = ReadUnixMilliseconds(item, "createdAt"),
            UpdatedAt = ReadUnixMilliseconds(
                item,
                "updatedAt",
                ReadUnixMilliseconds(item, "createdAt"))
        };
    }

    private static OrderStatus MapOrderStatus(
        string status,
        bool isActive,
        decimal filled,
        decimal requested)
    {
        if (filled > 0m && filled < requested)
        {
            return OrderStatus.PartiallyFilled;
        }

        if (isActive
            || status.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            return OrderStatus.Accepted;
        }

        if (requested > 0m && filled >= requested)
        {
            return OrderStatus.Filled;
        }

        return status.ToLowerInvariant() switch
        {
            "done" => OrderStatus.Cancelled,
            "cancelled" => OrderStatus.Cancelled,
            "canceled" => OrderStatus.Cancelled,
            "filled" => OrderStatus.Filled,
            _ => OrderStatus.Unknown
        };
    }

    private void ValidateConfiguration()
    {
        if (!Uri.TryCreate(
                _options.BaseUrl,
                UriKind.Absolute,
                out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(
                uri.Host,
                "api-futures.kucoin.com",
                StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Trim('/') != string.Empty)
        {
            throw new InvalidOperationException(
                "KuCoin Futures read-only adapter is restricted to the official live Futures API host.");
        }
    }

    private void ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)
            || string.IsNullOrWhiteSpace(_options.ApiSecret)
            || string.IsNullOrWhiteSpace(_options.Passphrase)
            || string.IsNullOrWhiteSpace(_options.KeyVersion))
        {
            throw new InvalidOperationException(
                "KuCoin Futures read-only ApiKey, ApiSecret, Passphrase and KeyVersion are required.");
        }
    }

    private static NotSupportedException ReadOnlyException() =>
        new(
            "KuCoin Futures adapter is read-only because the persistent Sandbox was suspended and the current /orders/test endpoint validates requests without creating queryable orders.");

    private static KeyValuePair<string, string?> Pair(
        string key,
        string? value) =>
        new(key, value);

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
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
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

    private static decimal? ReadPositiveDecimal(
        JsonElement element,
        string name)
    {
        var value = ReadDecimal(element, name);
        return value > 0m ? value : null;
    }

    private static bool ReadBoolean(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String =>
                bool.TryParse(value.GetString(), out var parsed)
                && parsed,
            _ => false
        };
    }

    private static DateTimeOffset ReadUnixMilliseconds(
        JsonElement element,
        string name,
        DateTimeOffset? fallback = null)
    {
        var raw = ReadString(element, name);

        return long.TryParse(
            raw,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var milliseconds)
            && milliseconds > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : fallback ?? DateTimeOffset.UtcNow;
    }
}
