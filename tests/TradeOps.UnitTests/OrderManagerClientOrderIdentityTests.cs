using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class OrderManagerClientOrderIdentityTests
{
    [Fact]
    public async Task ExistingOrder_WithExactExecutionIdentity_IsReusedWithoutRiskOrExchange()
    {
        var signal = NewSignal();
        var generator = new ClientOrderIdGenerator();
        var order = NewOrder(generator.Generate(signal.Id));
        var repository = new RaceOrderRepository(order);
        var risk = new StubRiskEngine(throwIfCalled: true);
        var exchange = new CountingExchangeClient();

        var manager = NewManager(repository, risk, exchange);

        var result = await manager.ExecuteSignalAsync(signal);

        Assert.True(result.Accepted);
        Assert.Equal(order.ClientOrderId, result.Order?.ClientOrderId);
        Assert.Equal(0, risk.CallCount);
        Assert.Equal(0, exchange.PlaceOrderCallCount);
    }

    [Theory]
    [InlineData("Symbol")]
    [InlineData("Side")]
    [InlineData("OrderType")]
    [InlineData("RequestedQuantity")]
    public async Task ExistingOrder_WithConflictingExecutionIdentity_ThrowsBeforeRiskOrExchange(
        string conflictingField)
    {
        var signal = NewSignal();
        var generator = new ClientOrderIdGenerator();
        var order = NewOrder(generator.Generate(signal.Id));

        switch (conflictingField)
        {
            case "Symbol":
                order.Symbol = "ETHUSDT";
                break;
            case "Side":
                order.Side = OrderSide.Sell;
                break;
            case "OrderType":
                order.OrderType = OrderType.Limit;
                break;
            case "RequestedQuantity":
                order.RequestedQuantity = 0.002m;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(conflictingField));
        }

        var repository = new RaceOrderRepository(order);
        var risk = new StubRiskEngine(throwIfCalled: true);
        var exchange = new CountingExchangeClient();
        var manager = NewManager(repository, risk, exchange);

        var exception = await Assert.ThrowsAsync<ClientOrderIdConflictException>(
            () => manager.ExecuteSignalAsync(signal));

        Assert.Equal(signal.Id, exception.SignalId);
        Assert.Equal(order.ClientOrderId, exception.ClientOrderId);
        Assert.Equal(order.Id, exception.ExistingOrderId);
        Assert.Contains(conflictingField, exception.ConflictingFields);
        Assert.Equal(0, risk.CallCount);
        Assert.Equal(0, exchange.PlaceOrderCallCount);
    }

    [Fact]
    public async Task DuplicateInsertRace_WithConflictingWinner_ThrowsBeforeExchangePlacement()
    {
        var signal = NewSignal();
        var generator = new ClientOrderIdGenerator();
        var winner = NewOrder(generator.Generate(signal.Id));
        winner.RequestedQuantity = 0.002m;

        var repository = new RaceOrderRepository(
            existing: null,
            duplicateWinner: winner);
        var risk = new StubRiskEngine();
        var exchange = new CountingExchangeClient();
        var manager = NewManager(repository, risk, exchange);

        var exception = await Assert.ThrowsAsync<ClientOrderIdConflictException>(
            () => manager.ExecuteSignalAsync(signal));

        Assert.Contains(nameof(Order.RequestedQuantity), exception.ConflictingFields);
        Assert.Equal(1, risk.CallCount);
        Assert.Equal(0, exchange.PlaceOrderCallCount);
    }

    private static OrderManager NewManager(
        IOrderRepository repository,
        IRiskEngine riskEngine,
        IExchangeClient exchangeClient) =>
        new(
            riskEngine,
            exchangeClient,
            repository,
            new ClientOrderIdGenerator(),
            new ThrowingOrderStateMachine(),
            new ThrowingAlertService(),
            NullLogger<OrderManager>.Instance);

    private static TradingSignal NewSignal() =>
        new()
        {
            Id = Guid.Parse("91919191-9191-4191-8191-919191919191"),
            Symbol = "BTCUSDT",
            Side = OrderSide.Buy,
            SignalType = "External",
            RequestedQuantity = 0.001m,
            CreatedAt = DateTimeOffset.UtcNow,
            Source = "OrderIdentityTest"
        };

    private static Order NewOrder(string clientOrderId) =>
        new()
        {
            Id = Guid.Parse("92929292-9292-4292-8292-929292929292"),
            ClientOrderId = clientOrderId,
            ExchangeOrderId = "existing-exchange-order",
            Symbol = "BTCUSDT",
            Side = OrderSide.Buy,
            OrderType = OrderType.Market,
            RequestedQuantity = 0.001m,
            FilledQuantity = 0m,
            Status = OrderStatus.Accepted,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private sealed class RaceOrderRepository(
        Order? existing = null,
        Order? duplicateWinner = null) : IOrderRepository
    {
        private Order? _existing = existing;
        private bool _firstLookup = true;

        public Task<Order?> GetByClientOrderIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default)
        {
            if (duplicateWinner is not null && _firstLookup)
            {
                _firstLookup = false;
                return Task.FromResult<Order?>(null);
            }

            _firstLookup = false;
            return Task.FromResult(_existing);
        }

        public Task<IReadOnlyCollection<Order>> GetReconciliationCandidatesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Order>>(Array.Empty<Order>());

        public Task<bool> TryAddAsync(
            Order order,
            CancellationToken cancellationToken = default)
        {
            if (duplicateWinner is not null)
            {
                _existing = duplicateWinner;
                return Task.FromResult(false);
            }

            _existing = order;
            return Task.FromResult(true);
        }

        public Task UpdateAsync(
            Order order,
            CancellationToken cancellationToken = default)
        {
            _existing = order;
            return Task.CompletedTask;
        }
    }

    private sealed class StubRiskEngine(bool throwIfCalled = false) : IRiskEngine
    {
        public int CallCount { get; private set; }

        public Task<RiskDecision> CheckAsync(
            TradingSignal signal,
            CancellationToken cancellationToken = default)
        {
            CallCount++;

            if (throwIfCalled)
            {
                throw new InvalidOperationException("Risk engine must not run when an existing deterministic order is reused or conflicts.");
            }

            return Task.FromResult(RiskDecision.Allowed());
        }
    }

    private sealed class CountingExchangeClient : IExchangeClient
    {
        public int PlaceOrderCallCount { get; private set; }

        public Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Exchange read must not run.");

        public Task<IReadOnlyCollection<Position>> GetPositionsAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Exchange read must not run.");

        public Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Order>>(Array.Empty<Order>());

        public Task<OrderResult> PlaceOrderAsync(
            PlaceOrderRequest request,
            CancellationToken cancellationToken = default)
        {
            PlaceOrderCallCount++;
            throw new InvalidOperationException("Exchange placement must not run in identity-conflict tests.");
        }

        public Task CancelOrderAsync(
            string exchangeOrderId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Exchange cancellation must not run.");

        public Task<Order?> GetOrderAsync(
            string exchangeOrderId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Exchange read must not run.");

        public Task<Order?> GetOrderByClientOrderIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Exchange read must not run.");
    }

    private sealed class ThrowingOrderStateMachine : IOrderStateMachine
    {
        public bool CanTransition(OrderStatus currentStatus, OrderStatus nextStatus) =>
            throw new InvalidOperationException("State machine must not run in identity-conflict tests.");

        public void Apply(
            Order order,
            OrderStatus nextStatus,
            decimal filledQuantity,
            decimal? averageFillPrice,
            string? exchangeOrderId = null,
            decimal? price = null) =>
            throw new InvalidOperationException("State machine must not run in identity-conflict tests.");
    }

    private sealed class ThrowingAlertService : IAlertService
    {
        public Task SendAsync(
            AlertMessage alert,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Alert service must not run in identity-conflict tests.");
    }
}
