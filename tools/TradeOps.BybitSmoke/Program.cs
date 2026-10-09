using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Bybit;

namespace TradeOps.BybitSmoke;

internal static class Program
{
    private const string TestnetBaseUrl = "https://api-testnet.bybit.com";
    private const string ExecutionConfirmation = "TESTNET_ONLY";

    public static async Task<int> Main()
    {
        try
        {
            var apiKey = RequireEnvironmentVariable("BYBIT_TESTNET_API_KEY");
            var apiSecret = RequireEnvironmentVariable("BYBIT_TESTNET_API_SECRET");
            var executeOrder = ReadBoolean("BYBIT_SMOKE_EXECUTE_ORDER");

            var options = new BybitOptions
            {
                BaseUrl = TestnetBaseUrl,
                ApiKey = apiKey,
                ApiSecret = apiSecret,
                Category = "linear",
                SettleCoin = "USDT",
                AccountType = "UNIFIED",
                RecvWindowMilliseconds = 5_000,
                HttpTimeoutSeconds = 10
            };

            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(options.HttpTimeoutSeconds)
            };

            var client = new BybitExchangeClient(
                new SingleHttpClientFactory(httpClient),
                options,
                NullLogger<BybitExchangeClient>.Instance);

            await client.EnsureConnectedAsync();
            var account = await client.GetAccountAsync();
            var positions = await client.GetPositionsAsync();
            var openOrders = await client.GetOpenOrdersAsync();

            Console.WriteLine("Bybit testnet authentication: OK");
            Console.WriteLine($"Account currency: {account.Currency}");
            Console.WriteLine($"Positions visible: {positions.Count}");
            Console.WriteLine($"Open orders visible: {openOrders.Count}");

            if (!executeOrder)
            {
                Console.WriteLine("Execution smoke disabled. Read-only verification completed.");
                return 0;
            }

            if (!string.Equals(
                    Environment.GetEnvironmentVariable("BYBIT_SMOKE_CONFIRM"),
                    ExecutionConfirmation,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Execution requires BYBIT_SMOKE_CONFIRM={ExecutionConfirmation}.");
            }

            var symbol = ReadOptional("BYBIT_SMOKE_SYMBOL", "BTCUSDT").ToUpperInvariant();
            var side = ParseSide(ReadOptional("BYBIT_SMOKE_SIDE", "Buy"));
            var quantity = ReadPositiveDecimal("BYBIT_SMOKE_QUANTITY");
            var limitPrice = ReadPositiveDecimal("BYBIT_SMOKE_LIMIT_PRICE");
            var clientOrderId = $"smk-{Guid.NewGuid():N}";

            Console.WriteLine(
                $"Submitting TESTNET limit order: {side} {quantity} {symbol} @ {limitPrice}; clientOrderId={clientOrderId}");

            var placement = await client.PlaceOrderAsync(new PlaceOrderRequest(
                clientOrderId,
                symbol,
                side,
                OrderType.Limit,
                quantity,
                limitPrice));

            if (placement.Status == OrderStatus.Rejected)
            {
                Console.Error.WriteLine("Bybit testnet placement was rejected.");
                return 3;
            }

            Console.WriteLine(
                $"Placement acknowledged: status={placement.Status}; exchangeOrderId={placement.ExchangeOrderId ?? "<none>"}");

            var exchangeOrder = await WaitForOrderAsync(client, clientOrderId);
            if (exchangeOrder is null)
            {
                Console.Error.WriteLine(
                    $"Order acknowledgement could not be confirmed by lookup. clientOrderId={clientOrderId}");
                return 4;
            }

            Console.WriteLine(
                $"Lookup confirmed: status={exchangeOrder.Status}; filled={exchangeOrder.FilledQuantity}/{exchangeOrder.RequestedQuantity}");

            if (IsTerminal(exchangeOrder.Status))
            {
                Console.WriteLine($"Order is already terminal: {exchangeOrder.Status}. No cancel request sent.");
                return 0;
            }

            if (string.IsNullOrWhiteSpace(exchangeOrder.ExchangeOrderId))
            {
                throw new InvalidOperationException(
                    $"Open testnet order {clientOrderId} has no exchange order id and cannot be cancelled safely.");
            }

            await client.CancelOrderAsync(exchangeOrder.ExchangeOrderId);
            Console.WriteLine("Cancel request acknowledged; confirming final exchange state...");

            var finalOrder = await WaitForTerminalOrderAsync(
                client,
                exchangeOrder.ExchangeOrderId,
                attempts: 15,
                delay: TimeSpan.FromSeconds(1));

            if (finalOrder is null)
            {
                Console.Error.WriteLine(
                    $"Could not confirm terminal state after cancel. Check Bybit testnet manually. clientOrderId={clientOrderId}");
                return 5;
            }

            Console.WriteLine(
                $"Final state confirmed: status={finalOrder.Status}; filled={finalOrder.FilledQuantity}/{finalOrder.RequestedQuantity}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Bybit testnet smoke failed: {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static async Task<Order?> WaitForOrderAsync(
        BybitExchangeClient client,
        string clientOrderId)
    {
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var order = await client.GetOrderByClientOrderIdAsync(clientOrderId);
            if (order is not null)
            {
                return order;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return null;
    }

    private static async Task<Order?> WaitForTerminalOrderAsync(
        BybitExchangeClient client,
        string exchangeOrderId,
        int attempts,
        TimeSpan delay)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var order = await client.GetOrderAsync(exchangeOrderId);
            if (order is not null && IsTerminal(order.Status))
            {
                return order;
            }

            await Task.Delay(delay);
        }

        return null;
    }

    private static bool IsTerminal(OrderStatus status) =>
        status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected;

    private static OrderSide ParseSide(string value) => value.ToUpperInvariant() switch
    {
        "BUY" => OrderSide.Buy,
        "SELL" => OrderSide.Sell,
        _ => throw new InvalidOperationException("BYBIT_SMOKE_SIDE must be Buy or Sell.")
    };

    private static decimal ReadPositiveDecimal(string name)
    {
        var value = RequireEnvironmentVariable(name);
        if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0m)
        {
            throw new InvalidOperationException($"{name} must be a positive decimal using '.' as the separator.");
        }

        return parsed;
    }

    private static bool ReadBoolean(string name) =>
        bool.TryParse(Environment.GetEnvironmentVariable(name), out var parsed) && parsed;

    private static string ReadOptional(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string RequireEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Required environment variable {name} is not set.");
        }

        return value.Trim();
    }

    private sealed class SingleHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }
}
