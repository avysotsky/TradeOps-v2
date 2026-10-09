using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Deribit;

public sealed class DeribitTestnetExchangeClient
    : IExchangeClient, IExchangeConnectionManager
{
    public const string HttpClientName = "deribit-testnet";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DeribitOptions _options;
    private readonly ILogger<DeribitTestnetExchangeClient> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;
    private volatile bool _isConnected;

    public DeribitTestnetExchangeClient(
        IHttpClientFactory httpClientFactory,
        DeribitOptions options,
        ILogger<DeribitTestnetExchangeClient> logger)
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
                "ExchangeReadinessSucceeded for Deribit testnet.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _isConnected = false;
            _logger.LogError(
                exception,
                "ExchangeReadinessFailed for Deribit testnet.");
            throw;
        }
    }

    public async Task<AccountInfo> GetAccountAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendPrivateGetAsync(
            "/private/get_account_summary",
            [
                Pair("currency", NormalizeCurrency(_options.AccountCurrency))
            ],
            cancellationToken);

        return new AccountInfo
        {
            Currency = NormalizeCurrency(_options.AccountCurrency),
            Balance = ReadDecimal(result, "balance"),
            Equity = ReadDecimal(result, "equity"),
            AvailableBalance = ReadDecimal(result, "available_funds")
        };
    }

    public async Task<IReadOnlyCollection<Position>> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendPrivateGetAsync(
            "/private/get_positions",
            [
                Pair("currency", "any"),
                Pair("kind", "future")
            ],
            cancellationToken);

        if (result.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Position>();
        }

        return result
            .EnumerateArray()
            .Select(MapPosition)
            .Where(position => position.Quantity > 0m)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendPrivateGetAsync(
            "/private/get_open_orders",
            [
                Pair("kind", "future")
            ],
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
        ValidateInstrumentName(request.Symbol);
        ValidateClientOrderId(request.ClientOrderId);

        if (request.Quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Deribit contract quantity must be positive.");
        }

        if (request.OrderType == OrderType.Limit
            && request.Price is null)
        {
            throw new ArgumentException(
                "Deribit limit orders require Price.",
                nameof(request));
        }

        if (request.OrderType is not (OrderType.Limit or OrderType.Market))
        {
            throw new NotSupportedException(
                $"Deribit adapter does not support TradeOps order type {request.OrderType}.");
        }

        var parameters = new List<KeyValuePair<string, string?>>
        {
            Pair("instrument_name", request.Symbol.Trim().ToUpperInvariant()),
            Pair("contracts", FormatDecimal(request.Quantity)),
            Pair(
                "type",
                request.OrderType == OrderType.Market
                    ? "market"
                    : "limit"),
            Pair("label", request.ClientOrderId)
        };

        if (request.Price is not null)
        {
            parameters.Add(
                Pair("price", FormatDecimal(request.Price.Value)));
        }

        var endpoint = request.Side == OrderSide.Buy
            ? "/private/buy"
            : "/private/sell";

        _logger.LogInformation(
            "OrderPlacementRequested for {ClientOrderId}: {Side} {Contracts} contracts {Instrument} on Deribit testnet.",
            request.ClientOrderId,
            request.Side,
            request.Quantity,
            request.Symbol);

        try
        {
            var result = await SendPrivateGetAsync(
                endpoint,
                parameters,
                cancellationToken);

            if (!result.TryGetProperty("order", out var orderElement))
            {
                throw new InvalidOperationException(
                    "Deribit order response did not contain an order.");
            }

            var order = MapOrder(orderElement);

            return new OrderResult(
                order.ExchangeOrderId,
                request.ClientOrderId,
                order.Status,
                order.FilledQuantity,
                order.AverageFillPrice);
        }
        catch (TaskCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Deribit order placement timed out for {request.ClientOrderId}.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TimeoutException(
                $"Deribit order placement outcome is ambiguous for {request.ClientOrderId}.",
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
                "Deribit order id is required.",
                nameof(exchangeOrderId));
        }

        _ = await SendPrivateGetAsync(
            "/private/cancel",
            [
                Pair("order_id", exchangeOrderId)
            ],
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
            var result = await SendPrivateGetAsync(
                "/private/get_order_state",
                [
                    Pair("order_id", exchangeOrderId)
                ],
                cancellationToken);

            return MapOrder(result);
        }
        catch (DeribitApiException exception)
            when (exception.Code is 10004 or 11044)
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
        ValidateClientOrderId(clientOrderId);

        var currency = !string.IsNullOrWhiteSpace(symbol)
            ? ExtractCurrency(symbol)
            : NormalizeCurrency(_options.AccountCurrency);

        try
        {
            var result = await SendPrivateGetAsync(
                "/private/get_order_state_by_label",
                [
                    Pair("currency", currency),
                    Pair("label", clientOrderId)
                ],
                cancellationToken);

            if (result.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var orders = result.EnumerateArray().ToArray();

            if (orders.Length == 0)
            {
                return null;
            }

            var selected = string.IsNullOrWhiteSpace(symbol)
                ? orders.OrderByDescending(
                        item => ReadInt64(item, "last_update_timestamp"))
                    .First()
                : orders.FirstOrDefault(
                    item => string.Equals(
                        ReadString(item, "instrument_name"),
                        symbol,
                        StringComparison.OrdinalIgnoreCase));

            return selected.ValueKind == JsonValueKind.Undefined
                ? null
                : MapOrder(selected);
        }
        catch (DeribitApiException exception)
            when (exception.Code is 10004 or 11044)
        {
            return null;
        }
    }

    internal static void ValidateInstrumentName(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)
            || !symbol.Contains(
                '-',
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Deribit adapter requires a native instrument name such as BTC-PERPETUAL.",
                nameof(symbol));
        }
    }

    internal static void ValidateClientOrderId(string clientOrderId)
    {
        if (string.IsNullOrWhiteSpace(clientOrderId)
            || clientOrderId.Length > 64)
        {
            throw new ArgumentException(
                "Deribit ClientOrderId/label must be 1..64 characters.",
                nameof(clientOrderId));
        }
    }

    private async Task<JsonElement> SendPrivateGetAsync(
        string path,
        IEnumerable<KeyValuePair<string, string?>> parameters,
        CancellationToken cancellationToken)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUrl(path, parameters));

        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                accessToken);

        return await SendAsync(
            request,
            cancellationToken);
    }

    private async Task<string> GetAccessTokenAsync(
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken)
            && DateTimeOffset.UtcNow < _accessTokenExpiresAt)
        {
            return _accessToken;
        }

        await _tokenLock.WaitAsync(cancellationToken);

        try
        {
            if (!string.IsNullOrWhiteSpace(_accessToken)
                && DateTimeOffset.UtcNow < _accessTokenExpiresAt)
            {
                return _accessToken;
            }

            ValidateCredentials();

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                BuildUrl(
                    "/public/auth",
                    [
                        Pair("grant_type", "client_credentials"),
                        Pair("client_id", _options.ClientId),
                        Pair("client_secret", _options.ClientSecret)
                    ]));

            var result = await SendAsync(
                request,
                cancellationToken);

            var token = ReadString(
                result,
                "access_token");

            var expiresIn = ReadInt64(
                result,
                "expires_in");

            if (string.IsNullOrWhiteSpace(token)
                || expiresIn <= 0)
            {
                throw new InvalidOperationException(
                    "Deribit authentication response did not contain a valid access token.");
            }

            _accessToken = token;
            _accessTokenExpiresAt =
                DateTimeOffset.UtcNow
                .AddSeconds(expiresIn)
                .AddSeconds(-30);

            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<JsonElement> SendAsync(
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
                _isConnected = false;
                throw new HttpRequestException(
                    $"Deribit testnet returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                throw new DeribitApiException(
                    (int)ReadInt64(error, "code"),
                    ReadString(error, "message"));
            }

            if (!root.TryGetProperty("result", out var result))
            {
                throw new InvalidOperationException(
                    "Deribit response did not contain result.");
            }

            _isConnected = true;
            return result.Clone();
        }
        catch (HttpRequestException)
        {
            _isConnected = false;
            throw;
        }
    }

    private string BuildUrl(
        string path,
        IEnumerable<KeyValuePair<string, string?>> parameters)
    {
        var query = string.Join(
            "&",
            parameters
                .Where(item => item.Value is not null)
                .Select(item =>
                    $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value ?? string.Empty)}"));

        var url =
            $"{_options.BaseUrl.TrimEnd('/')}{path}";

        return string.IsNullOrWhiteSpace(query)
            ? url
            : $"{url}?{query}";
    }

    private static Position MapPosition(JsonElement item)
    {
        var size = Math.Abs(ReadDecimal(item, "size"));

        return new Position
        {
            Symbol = ReadString(item, "instrument_name"),
            Side = ReadString(item, "direction")
                .Equals(
                    "sell",
                    StringComparison.OrdinalIgnoreCase)
                ? OrderSide.Sell
                : OrderSide.Buy,
            Quantity = size,
            AverageEntryPrice = ReadDecimal(
                item,
                "average_price"),
            MarkPrice = ReadDecimal(
                item,
                "mark_price"),
            UnrealizedPnL = ReadDecimal(
                item,
                "floating_profit_loss")
        };
    }

    private static Order MapOrder(JsonElement item)
    {
        var contracts = ReadDecimal(item, "contracts");
        var amount = ReadDecimal(item, "amount");
        var filledAmount = ReadDecimal(item, "filled_amount");

        var requestedQuantity = contracts > 0m
            ? contracts
            : amount;

        var filledQuantity =
            contracts > 0m && amount > 0m
                ? Math.Min(
                    contracts,
                    contracts
                    * filledAmount
                    / amount)
                : Math.Min(
                    requestedQuantity,
                    filledAmount);

        var state = ReadString(item, "order_state");

        if (state.Equals(
                "open",
                StringComparison.OrdinalIgnoreCase)
            && filledQuantity > 0m
            && filledQuantity < requestedQuantity)
        {
            state = "partially_filled";
        }

        return new Order
        {
            Id = Guid.NewGuid(),
            ExchangeOrderId = ReadString(
                item,
                "order_id"),
            ClientOrderId =
                string.IsNullOrWhiteSpace(
                    ReadString(item, "label"))
                    ? $"deribit-{ReadString(item, "order_id")}"
                    : ReadString(item, "label"),
            Symbol = ReadString(
                item,
                "instrument_name"),
            Side = ReadString(item, "direction")
                .Equals(
                    "sell",
                    StringComparison.OrdinalIgnoreCase)
                ? OrderSide.Sell
                : OrderSide.Buy,
            OrderType = ReadString(
                    item,
                    "order_type")
                .Equals(
                    "market",
                    StringComparison.OrdinalIgnoreCase)
                ? OrderType.Market
                : OrderType.Limit,
            RequestedQuantity = requestedQuantity,
            FilledQuantity = filledQuantity,
            AverageFillPrice =
                ReadPositiveNullableDecimal(
                    item,
                    "average_price"),
            Price =
                ReadPositiveNullableDecimal(
                    item,
                    "price"),
            Status = MapOrderStatus(state),
            CreatedAt = ReadTimestamp(
                item,
                "creation_timestamp"),
            UpdatedAt = ReadTimestamp(
                item,
                "last_update_timestamp",
                ReadTimestamp(
                    item,
                    "creation_timestamp"))
        };
    }

    private static OrderStatus MapOrderStatus(string state) =>
        state.ToLowerInvariant() switch
        {
            "open" => OrderStatus.Accepted,
            "partially_filled" => OrderStatus.PartiallyFilled,
            "filled" => OrderStatus.Filled,
            "cancelled" => OrderStatus.Cancelled,
            "canceled" => OrderStatus.Cancelled,
            "rejected" => OrderStatus.Rejected,
            "untriggered" => OrderStatus.Submitted,
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
                "test.deribit.com",
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                uri.AbsolutePath.TrimEnd('/'),
                "/api/v2",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Deribit adapter is restricted to the official testnet API endpoint.");
        }
    }

    private void ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId)
            || string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            throw new InvalidOperationException(
                "Deribit testnet ClientId and ClientSecret are not configured.");
        }
    }

    private static string ExtractCurrency(string symbol)
    {
        ValidateInstrumentName(symbol);

        return symbol
            .Split(
                '-',
                StringSplitOptions.RemoveEmptyEntries)[0]
            .ToUpperInvariant();
    }

    private static string NormalizeCurrency(string currency) =>
        currency.Trim().ToUpperInvariant();

    private static KeyValuePair<string, string?> Pair(
        string key,
        string? value) =>
        new(key, value);

    private static string FormatDecimal(decimal value) =>
        value.ToString(
            "0.############################",
            CultureInfo.InvariantCulture);

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
            JsonValueKind.String =>
                value.GetString() ?? string.Empty,
            JsonValueKind.Number =>
                value.GetRawText(),
            _ => string.Empty
        };
    }

    private static long ReadInt64(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String
            && long.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed)
            ? parsed
            : 0;
    }

    private static decimal ReadDecimal(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0m;
        }

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

    private static decimal? ReadPositiveNullableDecimal(
        JsonElement element,
        string name)
    {
        var value = ReadDecimal(element, name);
        return value > 0m ? value : null;
    }

    private static DateTimeOffset ReadTimestamp(
        JsonElement element,
        string name,
        DateTimeOffset? fallback = null)
    {
        var milliseconds = ReadInt64(element, name);

        return milliseconds > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(
                milliseconds)
            : fallback ?? DateTimeOffset.UtcNow;
    }
}
