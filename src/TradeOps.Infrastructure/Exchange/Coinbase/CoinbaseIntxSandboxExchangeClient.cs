using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Coinbase;

public sealed class CoinbaseIntxSandboxExchangeClient
    : IExchangeClient, IExchangeConnectionManager
{
    public const string HttpClientName = "coinbase-intx-sandbox";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CoinbaseIntxOptions _options;
    private readonly ILogger<CoinbaseIntxSandboxExchangeClient> _logger;
    private volatile bool _isConnected;

    public CoinbaseIntxSandboxExchangeClient(
        IHttpClientFactory httpClientFactory,
        CoinbaseIntxOptions options,
        ILogger<CoinbaseIntxSandboxExchangeClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        ValidateSandboxConfiguration();
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
                "ExchangeReadinessSucceeded for Coinbase INTX sandbox.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _isConnected = false;
            _logger.LogError(
                exception,
                "ExchangeReadinessFailed for Coinbase INTX sandbox.");
            throw;
        }
    }

    public async Task<AccountInfo> GetAccountAsync(
        CancellationToken cancellationToken = default)
    {
        var detail = await SendPrivateAsync(
            HttpMethod.Get,
            $"/portfolios/{PortfolioPath()}/detail",
            [],
            body: null,
            cancellationToken);

        if (!detail.TryGetProperty("summary", out var summary))
        {
            throw new InvalidOperationException(
                "Coinbase INTX portfolio detail did not contain summary.");
        }

        return new AccountInfo
        {
            Currency = _options.AccountCurrency.Trim().ToUpperInvariant(),
            Balance = ReadDecimal(summary, "collateral"),
            Equity = ReadDecimal(summary, "balance"),
            AvailableBalance = ReadDecimal(summary, "buying_power")
        };
    }

    public async Task<IReadOnlyCollection<Position>> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        var detail = await SendPrivateAsync(
            HttpMethod.Get,
            $"/portfolios/{PortfolioPath()}/detail",
            [],
            body: null,
            cancellationToken);

        if (!detail.TryGetProperty("positions", out var positions)
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
        var root = await SendPrivateAsync(
            HttpMethod.Get,
            "/orders",
            [
                Pair("portfolio", _options.PortfolioId)
            ],
            body: null,
            cancellationToken);

        if (root.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Order>();
        }

        return root
            .EnumerateArray()
            .Select(MapOrder)
            .ToArray();
    }

    public async Task<OrderResult> PlaceOrderAsync(
        PlaceOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateInstrument(request.Symbol);

        if (request.Quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Coinbase INTX quantity must be positive.");
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
                $"Coinbase INTX adapter does not support TradeOps order type {request.OrderType}.");
        }

        var exchangeClientOrderId =
            CoinbaseIntxClientOrderIdFactory.Create(
                request.ClientOrderId);

        var payload = new Dictionary<string, object?>
        {
            ["client_order_id"] = exchangeClientOrderId,
            ["side"] = request.Side == OrderSide.Buy ? "BUY" : "SELL",
            ["size"] = FormatDecimal(request.Quantity),
            ["tif"] = request.OrderType == OrderType.Market ? "IOC" : "GTC",
            ["instrument"] = request.Symbol.Trim().ToUpperInvariant(),
            ["type"] = request.OrderType == OrderType.Market ? "MARKET" : "LIMIT",
            ["portfolio"] = _options.PortfolioId
        };

        if (request.Price is not null)
        {
            payload["price"] = FormatDecimal(request.Price.Value);
        }

        _logger.LogInformation(
            "OrderPlacementRequested for {ClientOrderId}: {Side} {Quantity} {Instrument} on Coinbase INTX sandbox.",
            request.ClientOrderId,
            request.Side,
            request.Quantity,
            request.Symbol);

        try
        {
            var root = await SendPrivateAsync(
                HttpMethod.Post,
                "/orders",
                [],
                payload,
                cancellationToken);

            var order = MapOrder(root);
            return new OrderResult(
                order.ExchangeOrderId,
                request.ClientOrderId,
                order.Status,
                order.FilledQuantity,
                order.AverageFillPrice);
        }
        catch (CoinbaseIntxApiException exception)
        {
            _logger.LogWarning(
                exception,
                "Coinbase INTX sandbox explicitly rejected order {ClientOrderId}.",
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
                $"Coinbase INTX sandbox order placement timed out for {request.ClientOrderId}.",
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
                $"Coinbase INTX sandbox order placement outcome is ambiguous for {request.ClientOrderId}.",
                exception);
        }
    }

    public async Task CancelOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(exchangeOrderId))
        {
            throw new ArgumentException(
                "Coinbase INTX order id is required.",
                nameof(exchangeOrderId));
        }

        _ = await SendPrivateAsync(
            HttpMethod.Delete,
            $"/orders/{Uri.EscapeDataString(exchangeOrderId)}",
            [
                Pair("portfolio", _options.PortfolioId)
            ],
            body: null,
            cancellationToken);
    }

    public Task CancelOrderAsync(
        string exchangeOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        _ = symbol;
        return CancelOrderAsync(exchangeOrderId, cancellationToken);
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
            var root = await SendPrivateAsync(
                HttpMethod.Get,
                $"/orders/{Uri.EscapeDataString(exchangeOrderId)}",
                [
                    Pair("portfolio", _options.PortfolioId)
                ],
                body: null,
                cancellationToken);

            return MapOrder(root);
        }
        catch (CoinbaseIntxApiException exception)
            when (exception.StatusCode == 404)
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

    public Task<Order?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default) =>
        GetOrderByClientOrderIdAsync(
            clientOrderId,
            symbol: null,
            cancellationToken);

    public async Task<Order?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        _ = symbol;

        var exchangeClientOrderId =
            CoinbaseIntxClientOrderIdFactory.Create(clientOrderId);

        var root = await SendPrivateAsync(
            HttpMethod.Get,
            "/orders",
            [
                Pair("portfolio", _options.PortfolioId),
                Pair("client_order_id", exchangeClientOrderId)
            ],
            body: null,
            cancellationToken);

        if (root.ValueKind == JsonValueKind.Array)
        {
            var first = root.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object)
            {
                var order = MapOrder(first);
                order.ClientOrderId = clientOrderId;
                return order;
            }
        }

        var fills = await SendPrivateAsync(
            HttpMethod.Get,
            $"/portfolios/{PortfolioPath()}/fills",
            [
                Pair("client_order_id", exchangeClientOrderId),
                Pair("result_limit", "100")
            ],
            body: null,
            cancellationToken);

        return MapFilledOrderFromFills(
            fills,
            clientOrderId);
    }

    internal static void ValidateInstrument(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)
            || !symbol.Contains('-', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Coinbase INTX adapter requires a native instrument symbol such as BTC-PERP.",
                nameof(symbol));
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
                request.Symbol,
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
                "Coinbase INTX sandbox ambiguous placement recovery failed for {ClientOrderId}.",
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

        var timestamp = DateTimeOffset.UtcNow
            .ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

        var signingPath = "/api/v1" + path;
        var signature = CoinbaseIntxSigner.Sign(
            timestamp,
            method.Method,
            signingPath,
            bodyJson,
            _options.SigningKey);

        var url = $"{_options.BaseUrl.TrimEnd('/')}{path}";
        if (!string.IsNullOrWhiteSpace(query))
        {
            url += "?" + query;
        }

        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation(
            "CB-ACCESS-KEY",
            _options.AccessKey);
        request.Headers.TryAddWithoutValidation(
            "CB-ACCESS-PASSPHRASE",
            _options.Passphrase);
        request.Headers.TryAddWithoutValidation(
            "CB-ACCESS-SIGN",
            signature);
        request.Headers.TryAddWithoutValidation(
            "CB-ACCESS-TIMESTAMP",
            timestamp);
        request.Headers.TryAddWithoutValidation(
            "Accept",
            "application/json");

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
                    throw new HttpRequestException(
                        $"Coinbase INTX sandbox returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
                }

                throw new CoinbaseIntxApiException(
                    (int)response.StatusCode,
                    TryReadErrorMessage(json));
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

    private Position MapPosition(JsonElement item)
    {
        var netSize = ReadDecimal(item, "net_size");

        return new Position
        {
            Symbol = ReadString(item, "symbol"),
            Side = netSize < 0m ? OrderSide.Sell : OrderSide.Buy,
            Quantity = Math.Abs(netSize),
            AverageEntryPrice = ReadDecimal(item, "entry_vwap"),
            MarkPrice = ReadDecimal(item, "mark_price"),
            UnrealizedPnL = ReadDecimal(item, "unrealized_pnl")
        };
    }

    private Order MapOrder(JsonElement item)
    {
        var requested = Math.Abs(ReadDecimal(item, "size"));
        var executed = Math.Min(
            requested,
            Math.Abs(ReadDecimal(item, "exec_qty")));
        var leaves = Math.Abs(ReadDecimal(item, "leaves_qty"));

        if (executed == 0m
            && requested > 0m
            && leaves >= 0m
            && leaves <= requested)
        {
            executed = requested - leaves;
        }

        var orderId = ReadString(item, "order_id");
        var clientOrderId = ReadString(item, "client_order_id");
        var status = ReadString(item, "order_status");
        var eventType = ReadString(item, "event_type");

        return new Order
        {
            Id = Guid.NewGuid(),
            ExchangeOrderId = orderId,
            ClientOrderId = string.IsNullOrWhiteSpace(clientOrderId)
                ? $"coinbase-intx-{orderId}"
                : clientOrderId,
            Symbol = ReadString(item, "symbol"),
            Side = ReadString(item, "side")
                .Equals("SELL", StringComparison.OrdinalIgnoreCase)
                ? OrderSide.Sell
                : OrderSide.Buy,
            OrderType = ReadString(item, "type")
                .Equals("MARKET", StringComparison.OrdinalIgnoreCase)
                ? OrderType.Market
                : OrderType.Limit,
            RequestedQuantity = requested,
            FilledQuantity = executed,
            AverageFillPrice = ReadPositiveDecimal(item, "avg_price"),
            Price = ReadPositiveDecimal(item, "price"),
            Status = MapOrderStatus(
                status,
                eventType,
                executed,
                requested,
                leaves),
            CreatedAt = ReadIsoTimestamp(item, "submit_time"),
            UpdatedAt = ReadIsoTimestamp(
                item,
                "event_time",
                ReadIsoTimestamp(item, "submit_time"))
        };
    }

    private static Order? MapFilledOrderFromFills(
        JsonElement root,
        string originalClientOrderId)
    {
        if (!root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var fills = results.EnumerateArray().ToArray();
        if (fills.Length == 0)
        {
            return null;
        }

        var quantity = fills.Sum(fill =>
            Math.Abs(ReadDecimal(fill, "fill_qty")));

        if (quantity <= 0m)
        {
            return null;
        }

        var notional = fills.Sum(fill =>
            Math.Abs(ReadDecimal(fill, "fill_qty"))
            * ReadDecimal(fill, "fill_price"));

        var first = fills[0];
        var latest = fills
            .Select(fill => ReadIsoTimestamp(fill, "event_time"))
            .Max();

        return new Order
        {
            Id = Guid.NewGuid(),
            ExchangeOrderId = ReadString(first, "order_id"),
            ClientOrderId = originalClientOrderId,
            Symbol = ReadString(first, "symbol"),
            Side = ReadString(first, "side")
                .Equals("SELL", StringComparison.OrdinalIgnoreCase)
                ? OrderSide.Sell
                : OrderSide.Buy,
            OrderType = OrderType.Market,
            RequestedQuantity = quantity,
            FilledQuantity = quantity,
            AverageFillPrice = notional / quantity,
            Price = null,
            Status = OrderStatus.Filled,
            CreatedAt = latest,
            UpdatedAt = latest
        };
    }

    private static OrderStatus MapOrderStatus(
        string status,
        string eventType,
        decimal executed,
        decimal requested,
        decimal leaves)
    {
        if (executed > 0m
            && (leaves > 0m || executed < requested))
        {
            return OrderStatus.PartiallyFilled;
        }

        if (status.Equals("WORKING", StringComparison.OrdinalIgnoreCase))
        {
            return OrderStatus.Accepted;
        }

        if (executed > 0m
            && requested > 0m
            && executed >= requested)
        {
            return OrderStatus.Filled;
        }

        return eventType.ToUpperInvariant() switch
        {
            "REJECTED" => OrderStatus.Rejected,
            "CANCELED" => OrderStatus.Cancelled,
            "EXPIRED" => OrderStatus.Cancelled,
            "TRADE" when executed > 0m => OrderStatus.Filled,
            _ when status.Equals(
                "DONE",
                StringComparison.OrdinalIgnoreCase) =>
                    executed > 0m
                        ? OrderStatus.Filled
                        : OrderStatus.Cancelled,
            _ => OrderStatus.Unknown
        };
    }

    private void ValidateSandboxConfiguration()
    {
        if (!Uri.TryCreate(
                _options.BaseUrl,
                UriKind.Absolute,
                out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(
                uri.Host,
                "api-n5e1.coinbase.com",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                uri.AbsolutePath.TrimEnd('/'),
                "/api/v1",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Coinbase INTX adapter is restricted to the official sandbox REST endpoint.");
        }
    }

    private void ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(_options.AccessKey)
            || string.IsNullOrWhiteSpace(_options.Passphrase)
            || string.IsNullOrWhiteSpace(_options.SigningKey)
            || string.IsNullOrWhiteSpace(_options.PortfolioId))
        {
            throw new InvalidOperationException(
                "Coinbase INTX sandbox AccessKey, Passphrase, SigningKey and PortfolioId are required.");
        }
    }

    private string PortfolioPath() =>
        Uri.EscapeDataString(_options.PortfolioId);

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

    private static string TryReadErrorMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            foreach (var name in new[] { "message", "error", "msg" })
            {
                var value = ReadString(root, name);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        catch (JsonException)
        {
        }

        return string.IsNullOrWhiteSpace(json)
            ? "Unknown Coinbase INTX error."
            : json;
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
