using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Mexc;

public sealed class MexcFuturesReadOnlyExchangeClient
    : IExchangeClient, IExchangeConnectionManager
{
    public const string HttpClientName = "mexc-futures-readonly";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MexcOptions _options;
    private readonly ILogger<MexcFuturesReadOnlyExchangeClient> _logger;
    private readonly SemaphoreSlim _contractLock = new(1, 1);

    private IReadOnlyDictionary<string, MexcContractDto>? _contracts;
    private volatile bool _isConnected;

    public MexcFuturesReadOnlyExchangeClient(
        IHttpClientFactory httpClientFactory,
        MexcOptions options,
        ILogger<MexcFuturesReadOnlyExchangeClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        ValidateReadOnlyHost();
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
                "ExchangeReadinessSucceeded for MEXC Futures read-only adapter.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _isConnected = false;
            _logger.LogError(
                exception,
                "ExchangeReadinessFailed for MEXC Futures read-only adapter.");
            throw;
        }
    }

    public async Task<AccountInfo> GetAccountAsync(
        CancellationToken cancellationToken = default)
    {
        var currency = _options.SettlementCurrency
            .Trim()
            .ToUpperInvariant();

        var asset = await SendPrivateGetAsync<MexcAssetDto>(
            $"/api/v1/private/account/asset/{Uri.EscapeDataString(currency)}",
            [],
            cancellationToken);

        return new AccountInfo
        {
            Currency = asset.Currency,
            Balance = asset.CashBalance,
            Equity = asset.Equity,
            AvailableBalance = asset.AvailableBalance
        };
    }

    public async Task<IReadOnlyCollection<Position>> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        var positions = await SendPrivateGetAsync<MexcPositionDto[]>(
            "/api/v1/private/position/open_positions",
            [],
            cancellationToken);

        var contracts = await GetContractsAsync(cancellationToken);
        var result = new List<Position>(positions.Length);

        foreach (var item in positions)
        {
            if (!contracts.TryGetValue(item.Symbol, out var contract)
                || contract.ContractSize <= 0m
                || item.HoldVolume <= 0m)
            {
                continue;
            }

            var ticker = await SendPublicGetAsync<MexcTickerDto>(
                "/api/v1/contract/ticker",
                [
                    Pair("symbol", item.Symbol)
                ],
                cancellationToken);

            var quantity = item.HoldVolume * contract.ContractSize;
            var side = item.PositionType switch
            {
                1 => OrderSide.Buy,
                2 => OrderSide.Sell,
                _ => throw new InvalidOperationException(
                    $"Unsupported MEXC positionType '{item.PositionType}'.")
            };

            var unrealized = side == OrderSide.Buy
                ? (ticker.FairPrice - item.HoldAveragePrice) * quantity
                : (item.HoldAveragePrice - ticker.FairPrice) * quantity;

            result.Add(new Position
            {
                Symbol = ToTradeOpsSymbol(item.Symbol),
                Side = side,
                Quantity = quantity,
                AverageEntryPrice = item.HoldAveragePrice,
                MarkPrice = ticker.FairPrice,
                UnrealizedPnL = unrealized
            });
        }

        return result;
    }

    public async Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(
        CancellationToken cancellationToken = default)
    {
        var contracts = await GetContractsAsync(cancellationToken);
        var result = new List<Order>();

        for (var page = 1; page <= 20; page++)
        {
            var orders = await SendPrivateGetAsync<MexcOrderDto[]>(
                "/api/v1/private/order/list/open_orders",
                [
                    Pair("page_num", page.ToString(CultureInfo.InvariantCulture)),
                    Pair("page_size", "100")
                ],
                cancellationToken);

            foreach (var order in orders)
            {
                result.Add(MapOrder(order, contracts));
            }

            if (orders.Length < 100)
            {
                break;
            }
        }

        return result;
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
        try
        {
            var order = await SendPrivateGetAsync<MexcOrderDto>(
                $"/api/v1/private/order/get/{Uri.EscapeDataString(exchangeOrderId)}",
                [],
                cancellationToken);

            return MapOrder(
                order,
                await GetContractsAsync(cancellationToken));
        }
        catch (MexcApiException exception)
            when (exception.Code is 2040 or 1001)
        {
            return null;
        }
    }

    public Task<Order?> GetOrderAsync(
        string exchangeOrderId,
        string? symbol,
        CancellationToken cancellationToken = default) =>
        GetOrderAsync(exchangeOrderId, cancellationToken);

    public Task<Order?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default) =>
        throw new ArgumentException(
            "MEXC external order lookup requires symbol context.",
            nameof(clientOrderId));

    public async Task<Order?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            throw new ArgumentException(
                "MEXC external order lookup requires symbol context.",
                nameof(symbol));
        }

        var mexcSymbol = ToMexcSymbol(symbol);

        try
        {
            var order = await SendPrivateGetAsync<MexcOrderDto>(
                $"/api/v1/private/order/external/{Uri.EscapeDataString(mexcSymbol)}/{Uri.EscapeDataString(clientOrderId)}",
                [],
                cancellationToken);

            return MapOrder(
                order,
                await GetContractsAsync(cancellationToken));
        }
        catch (MexcApiException exception)
            when (exception.Code is 2040 or 1001)
        {
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, MexcContractDto>>
        GetContractsAsync(CancellationToken cancellationToken)
    {
        if (_contracts is not null)
        {
            return _contracts;
        }

        await _contractLock.WaitAsync(cancellationToken);

        try
        {
            if (_contracts is null)
            {
                var contracts = await SendPublicGetAsync<MexcContractDto[]>(
                    "/api/v1/contract/detail",
                    [],
                    cancellationToken);

                _contracts = contracts
                    .Where(contract => contract.ContractSize > 0m)
                    .ToDictionary(
                        contract => contract.Symbol,
                        StringComparer.OrdinalIgnoreCase);
            }
        }
        finally
        {
            _contractLock.Release();
        }

        return _contracts;
    }

    private async Task<T> SendPrivateGetAsync<T>(
        string path,
        IEnumerable<KeyValuePair<string, string?>> parameters,
        CancellationToken cancellationToken)
    {
        ValidateCredentials();

        var materialized = parameters
            .Where(item => item.Value is not null)
            .ToArray();

        var query = MexcSigner.BuildQueryString(materialized);
        var requestTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var signature = MexcSigner.Sign(
            _options.ApiKey,
            requestTime,
            query,
            _options.ApiSecret);

        var url = BuildUrl(path, query);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            url);

        request.Headers.TryAddWithoutValidation(
            "ApiKey",
            _options.ApiKey);
        request.Headers.TryAddWithoutValidation(
            "Request-Time",
            requestTime.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(
            "Signature",
            signature);
        request.Headers.TryAddWithoutValidation(
            "Recv-Window",
            Math.Clamp(_options.RecvWindowSeconds, 1, 60)
                .ToString(CultureInfo.InvariantCulture));

        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json");

        return await SendEnvelopeAsync<T>(
            request,
            cancellationToken);
    }

    private async Task<T> SendPublicGetAsync<T>(
        string path,
        IEnumerable<KeyValuePair<string, string?>> parameters,
        CancellationToken cancellationToken)
    {
        var query = MexcSigner.BuildQueryString(parameters);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            BuildUrl(path, query));

        return await SendEnvelopeAsync<T>(
            request,
            cancellationToken);
    }

    private async Task<T> SendEnvelopeAsync<T>(
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

                throw new HttpRequestException(
                    $"MEXC Contract API returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }

            var envelope =
                JsonSerializer.Deserialize<MexcEnvelope<T>>(
                    json,
                    JsonOptions)
                ?? throw new InvalidOperationException(
                    "MEXC Contract API returned an invalid JSON response.");

            if (envelope.Code != 0)
            {
                throw new MexcApiException(
                    envelope.Code,
                    envelope.Message ?? "Unknown MEXC error.");
            }

            if (envelope.Data is null)
            {
                throw new InvalidOperationException(
                    "MEXC Contract API response did not contain data.");
            }

            _isConnected = true;
            return envelope.Data;
        }
        catch (HttpRequestException)
        {
            _isConnected = false;
            throw;
        }
    }

    private Order MapOrder(
        MexcOrderDto order,
        IReadOnlyDictionary<string, MexcContractDto> contracts)
    {
        if (!contracts.TryGetValue(order.Symbol, out var contract))
        {
            throw new InvalidOperationException(
                $"MEXC contract metadata for '{order.Symbol}' is unavailable.");
        }

        var requestedQuantity =
            order.Volume * contract.ContractSize;

        var filledQuantity =
            Math.Min(
                requestedQuantity,
                order.DealVolume * contract.ContractSize);

        var createdAt = ParseTimestamp(order.CreateTime);
        var updatedAt = ParseTimestamp(
            order.UpdateTime,
            createdAt);

        return new Order
        {
            Id = Guid.NewGuid(),
            ExchangeOrderId =
                order.OrderId.ToString(CultureInfo.InvariantCulture),
            ClientOrderId =
                string.IsNullOrWhiteSpace(order.ExternalOrderId)
                    ? $"mexc-oid-{order.OrderId}"
                    : order.ExternalOrderId,
            Symbol = ToTradeOpsSymbol(order.Symbol),
            Side = MapSide(order.Side),
            OrderType = order.OrderType == 5
                ? OrderType.Market
                : OrderType.Limit,
            RequestedQuantity = requestedQuantity,
            FilledQuantity = filledQuantity,
            AverageFillPrice = order.DealAveragePrice > 0m
                ? order.DealAveragePrice
                : null,
            Price = order.Price > 0m
                ? order.Price
                : null,
            Status = MapOrderStatus(
                order.State,
                filledQuantity,
                requestedQuantity),
            CreatedAt = createdAt,
            UpdatedAt = updatedAt
        };
    }

    private static OrderStatus MapOrderStatus(
        int state,
        decimal filledQuantity,
        decimal requestedQuantity)
    {
        return state switch
        {
            1 => OrderStatus.Submitted,
            2 when filledQuantity > 0m
                && filledQuantity < requestedQuantity =>
                OrderStatus.PartiallyFilled,
            2 => OrderStatus.Accepted,
            3 => OrderStatus.Filled,
            4 => OrderStatus.Cancelled,
            5 => OrderStatus.Rejected,
            _ => OrderStatus.Unknown
        };
    }

    private static OrderSide MapSide(int side) =>
        side switch
        {
            1 or 2 => OrderSide.Buy,
            3 or 4 => OrderSide.Sell,
            _ => throw new InvalidOperationException(
                $"Unsupported MEXC order side '{side}'.")
        };

    internal static string ToMexcSymbol(string symbol)
    {
        var normalized = symbol
            .Trim()
            .ToUpperInvariant();

        if (normalized.Contains('_'))
        {
            return normalized;
        }

        foreach (var quote in new[] { "USDT", "USDC", "USD" })
        {
            if (normalized.EndsWith(
                    quote,
                    StringComparison.Ordinal)
                && normalized.Length > quote.Length)
            {
                return normalized[..^quote.Length]
                    + "_"
                    + quote;
            }
        }

        throw new ArgumentException(
            $"Cannot map TradeOps symbol '{symbol}' to a MEXC contract symbol.",
            nameof(symbol));
    }

    internal static string ToTradeOpsSymbol(string symbol) =>
        symbol.Replace(
            "_",
            string.Empty,
            StringComparison.Ordinal)
        .Trim()
        .ToUpperInvariant();

    private static DateTimeOffset ParseTimestamp(
        JsonElement element,
        DateTimeOffset? fallback = null)
    {
        long milliseconds = 0;

        if (element.ValueKind == JsonValueKind.Number)
        {
            _ = element.TryGetInt64(out milliseconds);
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString();

            if (!long.TryParse(
                    raw,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out milliseconds)
                && DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var parsedDate))
            {
                return parsedDate;
            }
        }

        return milliseconds > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : fallback ?? DateTimeOffset.UtcNow;
    }

    private string BuildUrl(
        string path,
        string query)
    {
        var url =
            $"{_options.BaseUrl.TrimEnd('/')}{path}";

        return string.IsNullOrWhiteSpace(query)
            ? url
            : $"{url}?{query}";
    }

    private void ValidateReadOnlyHost()
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
                "contract.mexc.com",
                StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Trim('/') != string.Empty)
        {
            throw new InvalidOperationException(
                "MEXC Futures read-only adapter is restricted to the official contract.mexc.com API host.");
        }
    }

    private void ValidateCredentials()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)
            || string.IsNullOrWhiteSpace(_options.ApiSecret))
        {
            throw new InvalidOperationException(
                "MEXC Futures read-only API credentials are not configured.");
        }
    }

    private static NotSupportedException ReadOnlyException() =>
        new(
            "MEXC Futures adapter is read-only. No sandbox/testnet execution environment is documented, so PlaceOrder/CancelOrder are intentionally disabled.");
    
    private static KeyValuePair<string, string?> Pair(
        string key,
        string? value) =>
        new(key, value);
}
