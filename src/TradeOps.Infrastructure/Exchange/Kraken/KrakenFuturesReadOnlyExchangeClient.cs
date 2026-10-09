using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Kraken;

public sealed class KrakenFuturesReadOnlyExchangeClient
    : IExchangeClient, IExchangeConnectionManager
{
    public const string HttpClientName = "kraken-futures-readonly";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly KrakenFuturesOptions _options;
    private readonly ILogger<KrakenFuturesReadOnlyExchangeClient> _logger;
    private volatile bool _isConnected;

    public KrakenFuturesReadOnlyExchangeClient(
        IHttpClientFactory httpClientFactory,
        KrakenFuturesOptions options,
        ILogger<KrakenFuturesReadOnlyExchangeClient> logger)
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
                "ExchangeReadinessSucceeded for Kraken Futures read-only.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _isConnected = false;
            _logger.LogError(
                exception,
                "ExchangeReadinessFailed for Kraken Futures read-only.");
            throw;
        }
    }

    public async Task<AccountInfo> GetAccountAsync(
        CancellationToken cancellationToken = default)
    {
        var root = await SendPrivateGetAsync(
            "/accounts",
            [],
            cancellationToken);

        if (!root.TryGetProperty("accounts", out var accounts)
            || accounts.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "Kraken Futures accounts response did not contain accounts.");
        }

        var currency = _options.AccountCurrency.Trim().ToUpperInvariant();

        if (accounts.TryGetProperty("flex", out var flex)
            && flex.ValueKind == JsonValueKind.Object)
        {
            return new AccountInfo
            {
                Currency = currency,
                Balance = ReadDecimal(flex, "balanceValue"),
                Equity = ReadDecimal(flex, "marginEquity"),
                AvailableBalance = ReadDecimal(flex, "availableMargin")
            };
        }

        if (accounts.TryGetProperty("cash", out var cash)
            && cash.TryGetProperty("balances", out var balances)
            && balances.ValueKind == JsonValueKind.Object
            && TryReadPropertyIgnoreCase(
                balances,
                currency,
                out var balanceElement))
        {
            var balance = ReadDecimal(balanceElement);
            return new AccountInfo
            {
                Currency = currency,
                Balance = balance,
                Equity = balance,
                AvailableBalance = balance
            };
        }

        throw new InvalidOperationException(
            $"Kraken Futures account currency '{currency}' was not found.");
    }

    public async Task<IReadOnlyCollection<Position>> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        var root = await SendPrivateGetAsync(
            "/openpositions",
            [],
            cancellationToken);

        if (!root.TryGetProperty("openPositions", out var positions)
            || positions.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Position>();
        }

        return positions
            .EnumerateArray()
            .Select(MapPosition)
            .Where(position => position.Quantity > 0m)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(
        CancellationToken cancellationToken = default)
    {
        var root = await SendPrivateGetAsync(
            "/openorders",
            [],
            cancellationToken);

        if (!root.TryGetProperty("openOrders", out var orders)
            || orders.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Order>();
        }

        return orders
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

        return (await GetOpenOrdersAsync(cancellationToken))
            .FirstOrDefault(order =>
                string.Equals(
                    order.ExchangeOrderId,
                    exchangeOrderId,
                    StringComparison.Ordinal));
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

        return (await GetOpenOrdersAsync(cancellationToken))
            .FirstOrDefault(order =>
                string.Equals(
                    order.ClientOrderId,
                    clientOrderId,
                    StringComparison.Ordinal));
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

        var query = string.Join(
            "&",
            parameters
                .Where(item => item.Value is not null)
                .Select(item =>
                    $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value ?? string.Empty)}"));

        var nonce = DateTimeOffset.UtcNow
            .ToUnixTimeMilliseconds()
            .ToString(CultureInfo.InvariantCulture);

        var endpointPath = "/api/v3" + endpoint;
        var authent = KrakenFuturesSigner.Sign(
            query,
            nonce,
            endpointPath,
            _options.ApiSecret);

        var url = $"{_options.BaseUrl.TrimEnd('/')}{endpoint}";
        if (!string.IsNullOrWhiteSpace(query))
        {
            url += "?" + query;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            url);

        request.Headers.TryAddWithoutValidation(
            "APIKey",
            _options.ApiKey);
        request.Headers.TryAddWithoutValidation(
            "Authent",
            authent);
        request.Headers.TryAddWithoutValidation(
            "Nonce",
            nonce);

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
                _isConnected = false;
                throw new HttpRequestException(
                    $"Kraken Futures returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("result", out var result)
                && !string.Equals(
                    result.GetString(),
                    "success",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Kraken Futures API error: {ReadString(root, "error")}");
            }

            _isConnected = true;
            return root.Clone();
        }
        catch (HttpRequestException)
        {
            _isConnected = false;
            throw;
        }
    }

    private static Position MapPosition(JsonElement item)
    {
        var size = ReadDecimal(item, "size");

        return new Position
        {
            Symbol = ReadString(item, "symbol"),
            Side = ReadString(item, "side")
                .Equals("short", StringComparison.OrdinalIgnoreCase)
                ? OrderSide.Sell
                : OrderSide.Buy,
            Quantity = Math.Abs(size),
            AverageEntryPrice = ReadDecimal(item, "price"),
            MarkPrice = ReadPositiveDecimal(item, "markPrice")
                ?? ReadDecimal(item, "price"),
            UnrealizedPnL = ReadDecimal(item, "unrealizedPnl")
        };
    }

    private static Order MapOrder(JsonElement item)
    {
        var filled = Math.Abs(ReadDecimal(item, "filledSize"));
        var unfilled = Math.Abs(ReadDecimal(item, "unfilledSize"));
        var requested = filled + unfilled;

        var orderId = ReadString(item, "order_id");
        var clientOrderId = ReadString(item, "cliOrdId");

        return new Order
        {
            Id = Guid.NewGuid(),
            ExchangeOrderId = orderId,
            ClientOrderId = string.IsNullOrWhiteSpace(clientOrderId)
                ? $"kraken-{orderId}"
                : clientOrderId,
            Symbol = ReadString(item, "symbol"),
            Side = ReadString(item, "side")
                .Equals("sell", StringComparison.OrdinalIgnoreCase)
                ? OrderSide.Sell
                : OrderSide.Buy,
            OrderType = ReadString(item, "orderType")
                .Equals("mkt", StringComparison.OrdinalIgnoreCase)
                ? OrderType.Market
                : OrderType.Limit,
            RequestedQuantity = requested,
            FilledQuantity = filled,
            AverageFillPrice = ReadPositiveDecimal(item, "avgPrice"),
            Price = ReadPositiveDecimal(item, "limitPrice"),
            Status = MapOrderStatus(
                ReadString(item, "status"),
                filled,
                requested),
            CreatedAt = ReadIsoTimestamp(item, "receivedTime"),
            UpdatedAt = ReadIsoTimestamp(
                item,
                "lastUpdateTime",
                ReadIsoTimestamp(item, "receivedTime"))
        };
    }

    private static OrderStatus MapOrderStatus(
        string status,
        decimal filled,
        decimal requested)
    {
        if (filled > 0m && filled < requested)
        {
            return OrderStatus.PartiallyFilled;
        }

        return status.ToLowerInvariant() switch
        {
            "untouched" => OrderStatus.Accepted,
            "placed" => OrderStatus.Accepted,
            "filled" => OrderStatus.Filled,
            "cancelled" => OrderStatus.Cancelled,
            "canceled" => OrderStatus.Cancelled,
            "rejected" => OrderStatus.Rejected,
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
                "futures.kraken.com",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                uri.AbsolutePath.TrimEnd('/'),
                "/derivatives/api/v3",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Kraken Futures read-only adapter is restricted to the official live Futures REST endpoint.");
        }
    }

    private void ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)
            || string.IsNullOrWhiteSpace(_options.ApiSecret))
        {
            throw new InvalidOperationException(
                "Kraken Futures read-only API credentials are not configured.");
        }
    }

    private static NotSupportedException ReadOnlyException() =>
        new(
            "Kraken Futures adapter is read-only because the previously documented public demo environment was scheduled for decommissioning and no replacement persistent demo endpoint is currently documented.");

    private static bool TryReadPropertyIgnoreCase(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(
                    property.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
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

    private static decimal ReadDecimal(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0m;
        }

        return ReadDecimal(value);
    }

    private static decimal ReadDecimal(JsonElement value)
    {
        var raw = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.String => value.GetString(),
            _ => null
        };

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

    private static DateTimeOffset ReadIsoTimestamp(
        JsonElement element,
        string name,
        DateTimeOffset? fallback = null)
    {
        var raw = ReadString(element, name);

        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : fallback ?? DateTimeOffset.UtcNow;
    }
}
