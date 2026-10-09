using System.Collections.Concurrent;
using System.Globalization;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Infrastructure.Exchange;

public sealed class MockExchangeClient : IExchangeClient, IExchangeConnectionManager
{
    private readonly ConcurrentDictionary<string, Order> _ordersByClientOrderId = new();
    private volatile bool _isConnected = true;

    private readonly List<Position> _positions =
    [
        new Position
        {
            Symbol = "BTCUSDT",
            Side = OrderSide.Buy,
            Quantity = 0.002m,
            AverageEntryPrice = 63_500m,
            MarkPrice = 64_000m,
            UnrealizedPnL = 1.00m
        }
    ];

    public bool IsConnected => _isConnected;

    public Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _isConnected = true;
        return Task.CompletedTask;
    }

    public Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisconnected();

        var account = new AccountInfo
        {
            Currency = "USDT",
            Balance = 10_000m,
            Equity = 10_001m,
            AvailableBalance = 8_500m
        };

        return Task.FromResult(account);
    }

    public Task<IReadOnlyCollection<Position>> GetPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisconnected();
        IReadOnlyCollection<Position> result = _positions.AsReadOnly();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisconnected();

        IReadOnlyCollection<Order> result = _ordersByClientOrderId.Values
            .Where(order => order.Status is OrderStatus.Created
                or OrderStatus.Submitted
                or OrderStatus.Accepted
                or OrderStatus.PartiallyFilled
                or OrderStatus.Unknown)
            .OrderBy(order => order.CreatedAt)
            .ToArray();

        return Task.FromResult(result);
    }

    public Task<OrderResult> PlaceOrderAsync(
        PlaceOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisconnected();

        var order = _ordersByClientOrderId.GetOrAdd(
            request.ClientOrderId,
            _ => CreatePartiallyFilledOrder(request));

        return Task.FromResult(ToResult(order));
    }

    public Task CancelOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisconnected();

        var order = FindByExchangeOrderId(exchangeOrderId)
            ?? RestoreFromExchangeOrderId(exchangeOrderId)
            ?? throw new KeyNotFoundException($"Order '{exchangeOrderId}' was not found.");

        _ordersByClientOrderId.TryAdd(order.ClientOrderId, order);

        lock (order)
        {
            if (order.Status != OrderStatus.Filled)
            {
                order.Status = OrderStatus.Cancelled;
                order.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        return Task.CompletedTask;
    }

    public Task<Order?> GetOrderAsync(
        string exchangeOrderId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisconnected();

        var order = FindByExchangeOrderId(exchangeOrderId)
            ?? RestoreFromExchangeOrderId(exchangeOrderId);

        if (order is not null)
        {
            _ordersByClientOrderId.TryAdd(order.ClientOrderId, order);
            AdvancePartialFill(order);
        }

        return Task.FromResult(order);
    }

    public Task<Order?> GetOrderByClientOrderIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisconnected();

        _ordersByClientOrderId.TryGetValue(clientOrderId, out var order);

        if (order is not null)
        {
            AdvancePartialFill(order);
        }

        return Task.FromResult(order);
    }

    public void SimulateDisconnect() => _isConnected = false;

    private void ThrowIfDisconnected()
    {
        if (!_isConnected)
        {
            throw new InvalidOperationException("Mock exchange is disconnected.");
        }
    }

    private Order? FindByExchangeOrderId(string exchangeOrderId)
    {
        return _ordersByClientOrderId.Values.FirstOrDefault(
            candidate => candidate.ExchangeOrderId == exchangeOrderId);
    }

    private static Order CreatePartiallyFilledOrder(PlaceOrderRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var averageFillPrice = request.Price ?? 64_000m;

        return new Order
        {
            Id = Guid.NewGuid(),
            ExchangeOrderId = BuildExchangeOrderId(request, averageFillPrice),
            ClientOrderId = request.ClientOrderId,
            Symbol = request.Symbol,
            Side = request.Side,
            OrderType = request.OrderType,
            RequestedQuantity = request.Quantity,
            FilledQuantity = request.Quantity * 0.60m,
            AverageFillPrice = averageFillPrice,
            Price = request.Price,
            Status = OrderStatus.PartiallyFilled,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static string BuildExchangeOrderId(
        PlaceOrderRequest request,
        decimal averageFillPrice)
    {
        return string.Join(
            '|',
            "mock",
            request.ClientOrderId,
            request.Symbol,
            (int)request.Side,
            (int)request.OrderType,
            request.Quantity.ToString(CultureInfo.InvariantCulture),
            averageFillPrice.ToString(CultureInfo.InvariantCulture));
    }

    private static Order? RestoreFromExchangeOrderId(string exchangeOrderId)
    {
        var parts = exchangeOrderId.Split('|');

        if (parts.Length != 7 || !string.Equals(parts[0], "mock", StringComparison.Ordinal))
        {
            return null;
        }

        if (!int.TryParse(parts[3], out var sideValue)
            || !Enum.IsDefined(typeof(OrderSide), sideValue)
            || !int.TryParse(parts[4], out var orderTypeValue)
            || !Enum.IsDefined(typeof(OrderType), orderTypeValue)
            || !decimal.TryParse(parts[5], NumberStyles.Number, CultureInfo.InvariantCulture, out var requestedQuantity)
            || !decimal.TryParse(parts[6], NumberStyles.Number, CultureInfo.InvariantCulture, out var averageFillPrice))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;

        return new Order
        {
            Id = Guid.NewGuid(),
            ExchangeOrderId = exchangeOrderId,
            ClientOrderId = parts[1],
            Symbol = parts[2],
            Side = (OrderSide)sideValue,
            OrderType = (OrderType)orderTypeValue,
            RequestedQuantity = requestedQuantity,
            FilledQuantity = requestedQuantity * 0.60m,
            AverageFillPrice = averageFillPrice,
            Status = OrderStatus.PartiallyFilled,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static void AdvancePartialFill(Order order)
    {
        lock (order)
        {
            if (order.Status != OrderStatus.PartiallyFilled)
            {
                return;
            }

            order.FilledQuantity = order.RequestedQuantity;
            order.Status = OrderStatus.Filled;
            order.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static OrderResult ToResult(Order order)
    {
        return new OrderResult(
            order.ExchangeOrderId,
            order.ClientOrderId,
            order.Status,
            order.FilledQuantity,
            order.AverageFillPrice);
    }
}
