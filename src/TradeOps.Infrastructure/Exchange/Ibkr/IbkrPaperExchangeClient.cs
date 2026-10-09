using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange.Ibkr;

public sealed class IbkrPaperExchangeClient
    : IExchangeClient, IExchangeConnectionManager
{
    public const string HttpClientName = "ibkr-paper-web-api";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IbkrOptions _options;
    private readonly ILogger<IbkrPaperExchangeClient> _logger;
    private readonly SemaphoreSlim _portfolioInitializationLock = new(1, 1);

    private string? _portfolioInitializedAccount;
    private volatile bool _isConnected;

    public IbkrPaperExchangeClient(
        IHttpClientFactory httpClientFactory,
        IbkrOptions options,
        ILogger<IbkrPaperExchangeClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        ValidatePaperConfiguration();
    }

    public bool IsConnected => _isConnected;

    public async Task EnsureConnectedAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var account = await EnsurePaperSessionAsync(
                cancellationToken);

            _isConnected = true;
            _logger.LogInformation(
                "ExchangeReadinessSucceeded for IBKR Paper account {AccountId}.",
                account);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _isConnected = false;
            _logger.LogError(
                exception,
                "ExchangeReadinessFailed for IBKR Paper Web API.");
            throw;
        }
    }

    public async Task<AccountInfo> GetAccountAsync(
        CancellationToken cancellationToken = default)
    {
        var accountId = await EnsurePaperSessionAsync(
            cancellationToken);

        var root = await SendJsonAsync(
            HttpMethod.Get,
            $"/iserver/account/{Uri.EscapeDataString(accountId)}/summary",
            body: null,
            cancellationToken);

        return new AccountInfo
        {
            Currency = _options.AccountCurrency
                .Trim()
                .ToUpperInvariant(),
            Balance = ReadFirstDecimal(
                root,
                "balance",
                "totalCashValue"),
            Equity = ReadFirstDecimal(
                root,
                "netLiquidationValue",
                "equityWithLoanValue"),
            AvailableBalance = ReadFirstDecimal(
                root,
                "availableFunds",
                "excessLiquidity")
        };
    }

    public async Task<IReadOnlyCollection<Position>> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        var accountId = await EnsurePaperSessionAsync(
            cancellationToken);

        await EnsurePortfolioInitializedAsync(
            accountId,
            cancellationToken);

        var root = await SendJsonAsync(
            HttpMethod.Get,
            $"/portfolio2/{Uri.EscapeDataString(accountId)}/positions",
            body: null,
            cancellationToken);

        if (root.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Position>();
        }

        return root
            .EnumerateArray()
            .Where(item =>
                string.Equals(
                    ReadFirstString(
                        item,
                        "assetClass",
                        "secType"),
                    "STK",
                    StringComparison.OrdinalIgnoreCase))
            .Select(MapPosition)
            .Where(position => position.Quantity > 0m)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(
        CancellationToken cancellationToken = default)
    {
        var accountId = await EnsurePaperSessionAsync(
            cancellationToken);

        return (await GetOrderSnapshotAsync(
                accountId,
                cancellationToken))
            .Where(order => !IsTerminal(order.Status))
            .ToArray();
    }

    public Task<OrderResult> PlaceOrderAsync(
        PlaceOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = request;
        _ = cancellationToken;
        throw MutationsDisabledException();
    }

    public Task CancelOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default)
    {
        _ = exchangeOrderId;
        _ = cancellationToken;
        throw MutationsDisabledException();
    }

    public Task CancelOrderAsync(
        string exchangeOrderId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        _ = exchangeOrderId;
        _ = symbol;
        _ = cancellationToken;
        throw MutationsDisabledException();
    }

    public async Task<Order?> GetOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(exchangeOrderId))
        {
            return null;
        }

        var accountId = await EnsurePaperSessionAsync(
            cancellationToken);

        try
        {
            var root = await SendJsonAsync(
                HttpMethod.Get,
                $"/iserver/account/order/status/{Uri.EscapeDataString(exchangeOrderId)}",
                body: null,
                cancellationToken);

            var returnedAccount = ReadString(
                root,
                "account");

            if (!string.IsNullOrWhiteSpace(returnedAccount)
                && !string.Equals(
                    returnedAccount,
                    accountId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "IBKR order status returned a different paper account.");
            }

            return MapOrder(root);
        }
        catch (HttpRequestException exception)
            when (exception.StatusCode is HttpStatusCode.NotFound
                or HttpStatusCode.ServiceUnavailable)
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
        return GetOrderAsync(
            exchangeOrderId,
            cancellationToken);
    }

    public async Task<Order?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientOrderId))
        {
            return null;
        }

        var accountId = await EnsurePaperSessionAsync(
            cancellationToken);

        return (await GetOrderSnapshotAsync(
                accountId,
                cancellationToken))
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

    private async Task<IReadOnlyCollection<Order>> GetOrderSnapshotAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        var root = await SendJsonAsync(
            HttpMethod.Get,
            $"/iserver/account/orders?force=true&accountId={Uri.EscapeDataString(accountId)}",
            body: null,
            cancellationToken);

        if (!root.TryGetProperty(
                "orders",
                out var orders)
            || orders.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Order>();
        }

        return orders
            .EnumerateArray()
            .Where(item =>
                string.Equals(
                    ReadFirstString(
                        item,
                        "secType",
                        "sec_type"),
                    "STK",
                    StringComparison.OrdinalIgnoreCase))
            .Where(item =>
            {
                var returnedAccount = ReadFirstString(
                    item,
                    "account",
                    "acct");

                return string.IsNullOrWhiteSpace(returnedAccount)
                    || string.Equals(
                        returnedAccount,
                        accountId,
                        StringComparison.OrdinalIgnoreCase);
            })
            .Select(MapOrder)
            .ToArray();
    }

    private async Task<string> EnsurePaperSessionAsync(
        CancellationToken cancellationToken)
    {
        var root = await SendJsonAsync(
            HttpMethod.Get,
            "/iserver/accounts",
            body: null,
            cancellationToken);

        if (!ReadBool(root, "isPaper"))
        {
            _isConnected = false;
            throw new InvalidOperationException(
                "IBKR adapter refuses non-Paper brokerage sessions.");
        }

        if (!root.TryGetProperty(
                "accounts",
                out var accountsElement)
            || accountsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "IBKR Paper session did not return tradable accounts.");
        }

        var accounts = accountsElement
            .EnumerateArray()
            .Select(value =>
                value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? string.Empty
                    : string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        if (accounts.Length == 0)
        {
            throw new InvalidOperationException(
                "IBKR Paper session has no tradable accounts.");
        }

        var configuredAccount = _options.AccountId.Trim();
        var selectedAccount = ReadString(
            root,
            "selectedAccount");

        string accountId;

        if (!string.IsNullOrWhiteSpace(configuredAccount))
        {
            accountId = accounts.FirstOrDefault(account =>
                    string.Equals(
                        account,
                        configuredAccount,
                        StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Configured IBKR Paper account '{configuredAccount}' is not available in the authenticated Paper session.");
        }
        else if (!string.IsNullOrWhiteSpace(selectedAccount)
            && accounts.Any(account =>
                string.Equals(
                    account,
                    selectedAccount,
                    StringComparison.OrdinalIgnoreCase)))
        {
            accountId = selectedAccount;
        }
        else if (accounts.Length == 1)
        {
            accountId = accounts[0];
        }
        else
        {
            throw new InvalidOperationException(
                "Multiple IBKR Paper accounts are available; configure Exchange:Ibkr:AccountId explicitly.");
        }

        if (!string.Equals(
                selectedAccount,
                accountId,
                StringComparison.OrdinalIgnoreCase)
            && accounts.Length > 1)
        {
            var switched = await SendJsonAsync(
                HttpMethod.Post,
                "/iserver/account",
                new { acctId = accountId },
                cancellationToken);

            if (!ReadBool(switched, "set")
                || !string.Equals(
                    ReadString(switched, "acctId"),
                    accountId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"IBKR Paper failed to select account '{accountId}'.");
            }
        }

        _isConnected = true;
        return accountId;
    }

    private async Task EnsurePortfolioInitializedAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
                _portfolioInitializedAccount,
                accountId,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await _portfolioInitializationLock.WaitAsync(
            cancellationToken);

        try
        {
            if (string.Equals(
                    _portfolioInitializedAccount,
                    accountId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var root = await SendJsonAsync(
                HttpMethod.Get,
                "/portfolio/accounts",
                body: null,
                cancellationToken);

            if (root.ValueKind != JsonValueKind.Array
                || !root.EnumerateArray().Any(item =>
                    string.Equals(
                        ReadFirstString(
                            item,
                            "accountId",
                            "id"),
                        accountId,
                        StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"IBKR portfolio preflight did not expose account '{accountId}'.");
            }

            _portfolioInitializedAccount = accountId;
        }
        finally
        {
            _portfolioInitializationLock.Release();
        }
    }

    private async Task<JsonElement> SendJsonAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            method,
            BuildUrl(path));

        request.Headers.TryAddWithoutValidation(
            "Accept",
            "application/json");

        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");
        }

        var client = _httpClientFactory.CreateClient(
            HttpClientName);

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
                    $"IBKR Web API returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                    null,
                    response.StatusCode);
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
        var signedQuantity = ReadDecimal(
            item,
            "position");

        return new Position
        {
            Symbol = ReadFirstString(
                item,
                "ticker",
                "contractDesc",
                "symbol")
                .Trim()
                .ToUpperInvariant(),
            Side = signedQuantity < 0m
                ? OrderSide.Sell
                : OrderSide.Buy,
            Quantity = Math.Abs(signedQuantity),
            AverageEntryPrice = ReadFirstDecimal(
                item,
                "avgPrice",
                "avgCost"),
            MarkPrice = ReadFirstDecimal(
                item,
                "mktPrice",
                "marketPrice"),
            UnrealizedPnL = ReadFirstDecimal(
                item,
                "unrealizedPnl",
                "unrealizedPnL")
        };
    }

    private Order MapOrder(JsonElement item)
    {
        var filled = Math.Abs(
            ReadFirstDecimal(
                item,
                "filledQuantity",
                "cum_fill"));
        var requested = Math.Abs(
            ReadFirstDecimal(
                item,
                "totalSize",
                "total_size"));
        var remaining = Math.Abs(
            ReadFirstDecimal(
                item,
                "remainingQuantity",
                "size"));

        if (requested <= 0m)
        {
            requested = filled + remaining;
        }

        var orderId = ReadFirstString(
            item,
            "orderId",
            "order_id");
        var clientOrderId = ReadFirstString(
            item,
            "order_ref",
            "cOID",
            "coid");

        var timestamp = ReadOrderTimestamp(item);

        return new Order
        {
            Id = Guid.NewGuid(),
            ExchangeOrderId = orderId,
            ClientOrderId = string.IsNullOrWhiteSpace(clientOrderId)
                ? $"ibkr-{orderId}"
                : clientOrderId,
            Symbol = ReadFirstString(
                item,
                "ticker",
                "symbol",
                "contract_description_1",
                "description1")
                .Trim()
                .ToUpperInvariant(),
            Side = ReadString(item, "side")
                .Equals(
                    "SELL",
                    StringComparison.OrdinalIgnoreCase)
                ? OrderSide.Sell
                : OrderSide.Buy,
            OrderType = ReadFirstString(
                item,
                "origOrderType",
                "orderType",
                "order_type")
                .Contains(
                    "MKT",
                    StringComparison.OrdinalIgnoreCase)
                || ReadFirstString(
                        item,
                        "origOrderType",
                        "orderType",
                        "order_type")
                    .Contains(
                        "MARKET",
                        StringComparison.OrdinalIgnoreCase)
                    ? OrderType.Market
                    : OrderType.Limit,
            RequestedQuantity = requested,
            FilledQuantity = Math.Min(
                filled,
                requested > 0m ? requested : filled),
            AverageFillPrice = ReadPositiveDecimal(
                item,
                "avgPrice",
                "average_price"),
            Price = ReadPositiveDecimal(
                item,
                "price",
                "limitPrice"),
            Status = MapOrderStatus(
                ReadFirstString(
                    item,
                    "status",
                    "order_status"),
                filled,
                requested),
            CreatedAt = timestamp,
            UpdatedAt = timestamp
        };
    }

    private static OrderStatus MapOrderStatus(
        string status,
        decimal filled,
        decimal requested)
    {
        if (filled > 0m
            && requested > filled)
        {
            return OrderStatus.PartiallyFilled;
        }

        return status
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant() switch
        {
            "pendingsubmit" => OrderStatus.Submitted,
            "apipending" => OrderStatus.Submitted,
            "presubmitted" => OrderStatus.Accepted,
            "submitted" => OrderStatus.Accepted,
            "pendingcancel" => OrderStatus.Accepted,
            "filled" => OrderStatus.Filled,
            "cancelled" => OrderStatus.Cancelled,
            "canceled" => OrderStatus.Cancelled,
            "apicancelled" => OrderStatus.Cancelled,
            "inactive" => OrderStatus.Rejected,
            "rejected" => OrderStatus.Rejected,
            _ => OrderStatus.Unknown
        };
    }

    private static bool IsTerminal(OrderStatus status) =>
        status is OrderStatus.Filled
            or OrderStatus.Cancelled
            or OrderStatus.Rejected;

    private void ValidatePaperConfiguration()
    {
        if (!Uri.TryCreate(
                _options.BaseUrl,
                UriKind.Absolute,
                out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(
                uri.Host,
                "localhost",
                StringComparison.OrdinalIgnoreCase)
            || uri.Port != 5000
            || !string.Equals(
                uri.AbsolutePath.TrimEnd('/'),
                "/v1/api",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "IBKR Paper adapter is restricted to the local Client Portal Gateway endpoint https://localhost:5000/v1/api.");
        }
    }

    private string BuildUrl(string path) =>
        $"{_options.BaseUrl.TrimEnd('/')}{path}";

    private static NotSupportedException MutationsDisabledException() =>
        new(
            "IBKR Paper order mutations are disabled in the first read-path milestone until order-reply confirmation and ambiguous-placement recovery are implemented and paper-tested.");

    private static bool ReadBool(
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
            JsonValueKind.String => bool.TryParse(
                value.GetString(),
                out var parsed)
                && parsed,
            _ => false
        };
    }

    private static string ReadFirstString(
        JsonElement element,
        params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadString(element, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
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

    private static decimal ReadFirstDecimal(
        JsonElement element,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            var parsed = ReadDecimal(value);
            if (parsed != 0m)
            {
                return parsed;
            }
        }

        return 0m;
    }

    private static decimal ReadDecimal(
        JsonElement element,
        string name) =>
        element.TryGetProperty(name, out var value)
            ? ReadDecimal(value)
            : 0m;

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
        params string[] names)
    {
        var value = ReadFirstDecimal(
            element,
            names);

        return value > 0m
            ? value
            : null;
    }

    private static DateTimeOffset ReadOrderTimestamp(
        JsonElement item)
    {
        if (item.TryGetProperty(
                "lastExecutionTime_r",
                out var epoch)
            && long.TryParse(
                epoch.ValueKind == JsonValueKind.Number
                    ? epoch.GetRawText()
                    : epoch.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var milliseconds)
            && milliseconds > 0)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(
                milliseconds);
        }

        var raw = ReadFirstString(
            item,
            "order_time",
            "lastExecutionTime");

        if (DateTimeOffset.TryParseExact(
                raw,
                "yyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal
                | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.UtcNow;
    }
}
