using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Hyperliquid;

public sealed class HyperliquidExchangeClient : IExchangeClient, IExchangeConnectionManager
{
    public const string HttpClientName = "hyperliquid-testnet";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HyperliquidOptions _options;
    private readonly ILogger<HyperliquidExchangeClient> _logger;
    private readonly SemaphoreSlim _metadataLock = new(1, 1);

    private IReadOnlyDictionary<string, HyperliquidAssetInfo>? _assets;
    private volatile bool _isConnected;

    public HyperliquidExchangeClient(
        IHttpClientFactory httpClientFactory,
        HyperliquidOptions options,
        ILogger<HyperliquidExchangeClient> logger)
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
                "ExchangeReadinessSucceeded for Hyperliquid testnet.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _isConnected = false;
            _logger.LogError(
                exception,
                "ExchangeReadinessFailed for Hyperliquid testnet.");
            throw;
        }
    }

    public async Task<AccountInfo> GetAccountAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await GetClearinghouseStateAsync(cancellationToken);

        return new AccountInfo
        {
            Currency = "USDC",
            Balance = ParseDecimal(state.MarginSummary.TotalRawUsd),
            Equity = ParseDecimal(state.MarginSummary.AccountValue),
            AvailableBalance = ParseDecimal(state.Withdrawable)
        };
    }

    public async Task<IReadOnlyCollection<Position>> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await GetClearinghouseStateAsync(cancellationToken);

        return state.AssetPositions
            .Select(item => MapPosition(item.Position))
            .Where(position => position.Quantity > 0m)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(
        CancellationToken cancellationToken = default)
    {
        var orders = await SendInfoAsync<HyperliquidOrderDto[]>(
            new
            {
                type = "frontendOpenOrders",
                user = _options.UserAddress
            },
            cancellationToken);

        return orders
            .Select(order => MapOrder(order, "open", order.Timestamp))
            .ToArray();
    }

    public async Task<OrderResult> PlaceOrderAsync(
        PlaceOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidatePrivateKey();

        var asset = await GetAssetInfoAsync(
            request.Symbol,
            cancellationToken);

        var size = NormalizeSize(
            request.Quantity,
            asset.SizeDecimals);

        var cloid = HyperliquidCloidFactory.Create(
            request.ClientOrderId);

        string price;
        string tif;

        if (request.OrderType == OrderType.Market)
        {
            var mid = await GetMidPriceAsync(
                asset.Name,
                cancellationToken);

            var multiplier = request.Side == OrderSide.Buy
                ? 1m + (_options.MarketSlippagePercent / 100m)
                : 1m - (_options.MarketSlippagePercent / 100m);

            var aggressivePrice = mid * multiplier;
            price = FormatWire(
                NormalizePerpPrice(
                    aggressivePrice,
                    asset.SizeDecimals));
            tif = "Ioc";
        }
        else if (request.OrderType == OrderType.Limit)
        {
            if (request.Price is null)
            {
                throw new ArgumentException(
                    "Hyperliquid limit orders require Price.",
                    nameof(request));
            }

            var normalizedPrice = NormalizePerpPrice(
                request.Price.Value,
                asset.SizeDecimals);

            if (normalizedPrice != request.Price.Value)
            {
                throw new ArgumentException(
                    "Hyperliquid limit price does not satisfy tick/significant-figure rules.",
                    nameof(request));
            }

            price = FormatWire(normalizedPrice);
            tif = "Gtc";
        }
        else
        {
            throw new NotSupportedException(
                $"Hyperliquid adapter does not support TradeOps order type {request.OrderType}.");
        }

        var nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var signature = HyperliquidL1Signer.SignOrderTestnet(
            _options.PrivateKey,
            nonce,
            asset.AssetId,
            request.Side == OrderSide.Buy,
            price,
            size,
            reduceOnly: false,
            tif,
            cloid);

        var action = new
        {
            type = "order",
            orders = new[]
            {
                new
                {
                    a = asset.AssetId,
                    b = request.Side == OrderSide.Buy,
                    p = price,
                    s = size,
                    r = false,
                    t = new
                    {
                        limit = new { tif }
                    },
                    c = cloid
                }
            },
            grouping = "na"
        };

        _logger.LogInformation(
            "OrderPlacementRequested for {ClientOrderId}: {Side} {Quantity} {Symbol} on Hyperliquid testnet.",
            request.ClientOrderId,
            request.Side,
            request.Quantity,
            request.Symbol);

        try
        {
            using var document = await SendExchangeAsync(
                action,
                nonce,
                signature,
                cancellationToken);

            if (TryParseOrderResult(
                    document.RootElement,
                    request.ClientOrderId,
                    out var result))
            {
                return result;
            }

            var existing = await GetOrderByClientOrderIdAsync(
                request.ClientOrderId,
                request.Symbol,
                cancellationToken);

            if (existing is not null)
            {
                return new OrderResult(
                    existing.ExchangeOrderId,
                    request.ClientOrderId,
                    existing.Status,
                    existing.FilledQuantity,
                    existing.AverageFillPrice);
            }

            return new OrderResult(
                null,
                request.ClientOrderId,
                OrderStatus.Rejected,
                0m,
                null);
        }
        catch (HyperliquidApiException exception)
        {
            _logger.LogWarning(
                exception,
                "Hyperliquid explicitly rejected order {ClientOrderId}.",
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
            throw new TimeoutException(
                $"Hyperliquid order placement timed out for {request.ClientOrderId}.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TimeoutException(
                $"Hyperliquid order placement outcome is ambiguous for {request.ClientOrderId}.",
                exception);
        }
    }

    public async Task CancelOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetOrderAsync(
            exchangeOrderId,
            cancellationToken);

        if (existing is null)
        {
            throw new KeyNotFoundException(
                $"Hyperliquid order '{exchangeOrderId}' was not found.");
        }

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
        ValidatePrivateKey();

        if (!long.TryParse(
                exchangeOrderId,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var orderId))
        {
            throw new ArgumentException(
                "Hyperliquid exchange order id must be numeric.",
                nameof(exchangeOrderId));
        }

        if (string.IsNullOrWhiteSpace(symbol))
        {
            var existing = await GetOrderAsync(
                exchangeOrderId,
                cancellationToken);

            symbol = existing?.Symbol
                ?? throw new InvalidOperationException(
                    "Hyperliquid cancellation requires a resolvable symbol.");
        }

        var asset = await GetAssetInfoAsync(
            symbol,
            cancellationToken);

        var nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var signature = HyperliquidL1Signer.SignCancelTestnet(
            _options.PrivateKey,
            nonce,
            asset.AssetId,
            orderId);

        var action = new
        {
            type = "cancel",
            cancels = new[]
            {
                new
                {
                    a = asset.AssetId,
                    o = orderId
                }
            }
        };

        using var document = await SendExchangeAsync(
            action,
            nonce,
            signature,
            cancellationToken);

        EnsureCancelSucceeded(document.RootElement);

        _logger.LogInformation(
            "OrderCancellationAcknowledged for Hyperliquid order {ExchangeOrderId} on {Symbol}.",
            exchangeOrderId,
            asset.Name);
    }

    public Task<Order?> GetOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default) =>
        GetOrderAsync(exchangeOrderId, symbol: null, cancellationToken);

    public async Task<Order?> GetOrderAsync(
        string exchangeOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        _ = symbol;

        object oid = long.TryParse(
            exchangeOrderId,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : exchangeOrderId;

        return await GetOrderStatusAsync(oid, cancellationToken);
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

        var cloid = HyperliquidCloidFactory.IsValid(clientOrderId)
            ? clientOrderId
            : HyperliquidCloidFactory.Create(clientOrderId);

        return await GetOrderStatusAsync(
            cloid,
            cancellationToken);
    }

    internal static string NormalizeSize(
        decimal quantity,
        int sizeDecimals)
    {
        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quantity),
                "Hyperliquid order quantity must be positive.");
        }

        var rounded = decimal.Round(
            quantity,
            sizeDecimals,
            MidpointRounding.ToEven);

        if (rounded != quantity)
        {
            throw new ArgumentException(
                $"Hyperliquid order quantity exceeds szDecimals={sizeDecimals}.",
                nameof(quantity));
        }

        return FormatWire(rounded);
    }

    internal static decimal NormalizePerpPrice(
        decimal price,
        int sizeDecimals)
    {
        if (price <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(price),
                "Hyperliquid order price must be positive.");
        }

        // Hyperliquid explicitly allows integer prices regardless of
        // significant-figure count.
        if (decimal.Truncate(price) == price)
        {
            return price;
        }

        var magnitude = (int)Math.Floor(
            Math.Log10((double)Math.Abs(price)));

        var significantScale = 4 - magnitude;
        decimal significantRounded;

        if (significantScale >= 0)
        {
            significantRounded = decimal.Round(
                price,
                Math.Min(significantScale, 28),
                MidpointRounding.ToEven);
        }
        else
        {
            var factor = Pow10(-significantScale);
            significantRounded =
                decimal.Round(
                    price / factor,
                    0,
                    MidpointRounding.ToEven)
                * factor;
        }

        var maxDecimals = Math.Max(
            0,
            6 - sizeDecimals);

        return decimal.Round(
            significantRounded,
            maxDecimals,
            MidpointRounding.ToEven);
    }

    private async Task<HyperliquidClearinghouseStateDto>
        GetClearinghouseStateAsync(CancellationToken cancellationToken)
    {
        return await SendInfoAsync<HyperliquidClearinghouseStateDto>(
            new
            {
                type = "clearinghouseState",
                user = _options.UserAddress
            },
            cancellationToken);
    }

    private async Task<HyperliquidAssetInfo> GetAssetInfoAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeSymbol(symbol);

        if (normalized.Contains(':'))
        {
            throw new NotSupportedException(
                "Builder-deployed Hyperliquid perps are not supported in this milestone.");
        }

        if (_assets is null)
        {
            await _metadataLock.WaitAsync(cancellationToken);
            try
            {
                if (_assets is null)
                {
                    var meta = await SendInfoAsync<HyperliquidMetaDto>(
                        new { type = "meta" },
                        cancellationToken);

                    _assets = meta.Universe
                        .Select((asset, index) => new HyperliquidAssetInfo(
                            index,
                            NormalizeSymbol(asset.Name),
                            asset.SizeDecimals))
                        .ToDictionary(
                            asset => asset.Name,
                            StringComparer.OrdinalIgnoreCase);
                }
            }
            finally
            {
                _metadataLock.Release();
            }
        }

        return _assets.TryGetValue(normalized, out var result)
            ? result
            : throw new KeyNotFoundException(
                $"Hyperliquid testnet asset '{normalized}' was not found in meta universe.");
    }

    private async Task<decimal> GetMidPriceAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        var mids = await SendInfoAsync<Dictionary<string, string>>(
            new { type = "allMids" },
            cancellationToken);

        if (!mids.TryGetValue(symbol, out var raw)
            || !decimal.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var mid)
            || mid <= 0m)
        {
            throw new InvalidOperationException(
                $"Hyperliquid testnet mid price for '{symbol}' is unavailable.");
        }

        return mid;
    }

    private async Task<Order?> GetOrderStatusAsync(
        object oid,
        CancellationToken cancellationToken)
    {
        var response = await SendInfoAsync<HyperliquidOrderStatusEnvelopeDto>(
            new
            {
                type = "orderStatus",
                user = _options.UserAddress,
                oid
            },
            cancellationToken);

        if (!string.Equals(
                response.Status,
                "order",
                StringComparison.OrdinalIgnoreCase)
            || response.Order is null)
        {
            return null;
        }

        return MapOrder(
            response.Order.Order,
            response.Order.Status,
            response.Order.StatusTimestamp);
    }

    private async Task<T> SendInfoAsync<T>(
        object payload,
        CancellationToken cancellationToken)
    {
        ValidateUserAddress();

        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var response = await client.PostAsJsonAsync(
            BuildInfoUri(),
            payload,
            JsonOptions,
            cancellationToken);

        var json = await response.Content.ReadAsStringAsync(
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _isConnected = false;
            throw new HttpRequestException(
                $"Hyperliquid returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        var result = JsonSerializer.Deserialize<T>(
            json,
            JsonOptions)
            ?? throw new InvalidOperationException(
                "Hyperliquid returned an empty or invalid JSON response.");

        _isConnected = true;
        return result;
    }

    private async Task<JsonDocument> SendExchangeAsync(
        object action,
        long nonce,
        HyperliquidSignature signature,
        CancellationToken cancellationToken)
    {
        ValidateUserAddress();

        var client = _httpClientFactory.CreateClient(HttpClientName);

        var payload = new
        {
            action,
            nonce,
            signature = new
            {
                r = signature.R,
                s = signature.S,
                v = signature.V
            },
            vaultAddress = (string?)null,
            expiresAfter = (long?)null
        };

        using var response = await client.PostAsJsonAsync(
            BuildExchangeUri(),
            payload,
            JsonOptions,
            cancellationToken);

        var json = await response.Content.ReadAsStringAsync(
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode >= 500)
            {
                _isConnected = false;
            }

            throw new HyperliquidApiException(
                $"Hyperliquid exchange endpoint returned HTTP {(int)response.StatusCode} ({response.StatusCode}): {json}");
        }

        _isConnected = true;
        return JsonDocument.Parse(json);
    }

    private Position MapPosition(HyperliquidPositionDto position)
    {
        var signedSize = ParseDecimal(position.SignedSize);
        var quantity = Math.Abs(signedSize);
        var positionValue = Math.Abs(
            ParseDecimal(position.PositionValue));

        var markPrice = quantity > 0m
            ? positionValue / quantity
            : 0m;

        return new Position
        {
            Symbol = NormalizeSymbol(position.Coin),
            Side = signedSize >= 0m
                ? OrderSide.Buy
                : OrderSide.Sell,
            Quantity = quantity,
            AverageEntryPrice = ParseDecimal(position.EntryPrice),
            MarkPrice = markPrice,
            UnrealizedPnL = ParseDecimal(position.UnrealizedPnl)
        };
    }

    private static Order MapOrder(
        HyperliquidOrderDto order,
        string status,
        long statusTimestamp)
    {
        var originalQuantity = ParseDecimal(
            order.OriginalSize);

        var remainingQuantity = ParseDecimal(
            order.RemainingSize);

        var filledQuantity = Math.Max(
            0m,
            originalQuantity - remainingQuantity);

        var timestamp = statusTimestamp > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(
                statusTimestamp)
            : DateTimeOffset.UtcNow;

        return new Order
        {
            Id = Guid.NewGuid(),
            ExchangeOrderId =
                order.OrderId.ToString(
                    CultureInfo.InvariantCulture),
            ClientOrderId =
                string.IsNullOrWhiteSpace(order.ClientOrderId)
                    ? $"hl-oid-{order.OrderId}"
                    : order.ClientOrderId,
            Symbol = NormalizeSymbol(order.Coin),
            Side = MapSide(order.Side),
            OrderType = MapOrderType(order.OrderType),
            RequestedQuantity = originalQuantity,
            FilledQuantity = filledQuantity,
            AverageFillPrice = null,
            Price = ParsePositiveNullableDecimal(
                order.LimitPrice),
            Status = MapOrderStatus(status),
            CreatedAt = order.Timestamp > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(
                    order.Timestamp)
                : timestamp,
            UpdatedAt = timestamp
        };
    }

    private static bool TryParseOrderResult(
        JsonElement root,
        string clientOrderId,
        out OrderResult result)
    {
        result = new OrderResult(
            null,
            clientOrderId,
            OrderStatus.Rejected,
            0m,
            null);

        if (!root.TryGetProperty("status", out var status)
            || !string.Equals(
                status.GetString(),
                "ok",
                StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("response", out var response)
            || !response.TryGetProperty("data", out var data)
            || !data.TryGetProperty("statuses", out var statuses)
            || statuses.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var first = statuses.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (first.TryGetProperty("resting", out var resting))
        {
            var oid = ReadOrderId(resting);
            result = new OrderResult(
                oid,
                clientOrderId,
                OrderStatus.Accepted,
                0m,
                null);
            return true;
        }

        if (first.TryGetProperty("filled", out var filled))
        {
            var oid = ReadOrderId(filled);
            var totalSize = ReadDecimal(
                filled,
                "totalSz");
            var averagePrice = ReadNullableDecimal(
                filled,
                "avgPx");

            result = new OrderResult(
                oid,
                clientOrderId,
                OrderStatus.Filled,
                totalSize,
                averagePrice);
            return true;
        }

        return false;
    }

    private static void EnsureCancelSucceeded(
        JsonElement root)
    {
        if (!root.TryGetProperty("status", out var status)
            || !string.Equals(
                status.GetString(),
                "ok",
                StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("response", out var response)
            || !response.TryGetProperty("data", out var data)
            || !data.TryGetProperty("statuses", out var statuses)
            || statuses.ValueKind != JsonValueKind.Array)
        {
            throw new HyperliquidApiException(
                "Hyperliquid cancellation response was invalid.");
        }

        var first = statuses.EnumerateArray().FirstOrDefault();

        if (first.ValueKind != JsonValueKind.String
            || !string.Equals(
                first.GetString(),
                "success",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new HyperliquidApiException(
                $"Hyperliquid cancellation failed: {first.GetRawText()}");
        }
    }

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
                "api.hyperliquid-testnet.xyz",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Hyperliquid adapter is restricted to the official testnet API host.");
        }
    }

    private void ValidateUserAddress()
    {
        var address = _options.UserAddress?.Trim();

        if (string.IsNullOrWhiteSpace(address)
            || address.Length != 42
            || !address.StartsWith(
                "0x",
                StringComparison.OrdinalIgnoreCase)
            || !address.AsSpan(2).ToString().All(
                Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                "Hyperliquid testnet UserAddress must be a 42-character hexadecimal onchain address.");
        }
    }

    private void ValidatePrivateKey()
    {
        var value = _options.PrivateKey?.Trim();
        var hex = value?.StartsWith(
            "0x",
            StringComparison.OrdinalIgnoreCase) == true
            ? value[2..]
            : value;

        if (string.IsNullOrWhiteSpace(hex)
            || hex.Length != 64
            || !hex.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                "Hyperliquid testnet PrivateKey must be configured for order placement/cancellation.");
        }
    }

    private string BuildInfoUri() =>
        $"{_options.BaseUrl.TrimEnd('/')}/info";

    private string BuildExchangeUri() =>
        $"{_options.BaseUrl.TrimEnd('/')}/exchange";

    private static string NormalizeSymbol(string coin) =>
        coin.Trim().ToUpperInvariant();

    private static string FormatWire(decimal value) =>
        value.ToString(
            "0.############################",
            CultureInfo.InvariantCulture);

    private static decimal Pow10(int exponent)
    {
        var result = 1m;

        for (var i = 0; i < exponent; i++)
        {
            result *= 10m;
        }

        return result;
    }

    private static OrderSide MapSide(string side) =>
        side.ToUpperInvariant() switch
        {
            "B" => OrderSide.Buy,
            "A" => OrderSide.Sell,
            _ => throw new InvalidOperationException(
                $"Unsupported Hyperliquid side '{side}'.")
        };

    private static OrderType MapOrderType(
        string orderType) =>
        orderType.ToUpperInvariant() switch
        {
            "MARKET" => OrderType.Market,
            _ => OrderType.Limit
        };

    private static OrderStatus MapOrderStatus(
        string status) =>
        status.ToLowerInvariant() switch
        {
            "open" => OrderStatus.Accepted,
            "filled" => OrderStatus.Filled,
            "rejected" => OrderStatus.Rejected,
            "triggered" => OrderStatus.Accepted,
            "canceled" => OrderStatus.Cancelled,
            "margincanceled" => OrderStatus.Cancelled,
            "vaultwithdrawalcanceled" => OrderStatus.Cancelled,
            "openinterestcapcanceled" => OrderStatus.Cancelled,
            "selftradecanceled" => OrderStatus.Cancelled,
            "reduceonlycanceled" => OrderStatus.Cancelled,
            "siblingfilledcanceled" => OrderStatus.Cancelled,
            "delistedcanceled" => OrderStatus.Cancelled,
            "liquidatedcanceled" => OrderStatus.Cancelled,
            "scheduledcancel" => OrderStatus.Cancelled,
            _ when status.EndsWith(
                "Rejected",
                StringComparison.OrdinalIgnoreCase) =>
                OrderStatus.Rejected,
            _ => OrderStatus.Unknown
        };

    private static string? ReadOrderId(
        JsonElement element)
    {
        if (!element.TryGetProperty(
                "oid",
                out var oid))
        {
            return null;
        }

        return oid.ValueKind switch
        {
            JsonValueKind.Number => oid.GetRawText(),
            JsonValueKind.String => oid.GetString(),
            _ => null
        };
    }

    private static decimal ReadDecimal(
        JsonElement element,
        string name) =>
        ReadNullableDecimal(element, name) ?? 0m;

    private static decimal? ReadNullableDecimal(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(
                name,
                out var value))
        {
            return null;
        }

        var raw = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };

        return decimal.TryParse(
            raw,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    private static decimal ParseDecimal(
        string? value) =>
        decimal.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : 0m;

    private static decimal? ParsePositiveNullableDecimal(
        string? value)
    {
        var parsed = ParseDecimal(value);
        return parsed > 0m ? parsed : null;
    }
}
