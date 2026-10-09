using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class OrderCancellationServiceTests
{
    [Fact]
    public async Task CancelAsync_ActiveOrder_CancelsAndPersistsConfirmedState()
    {
        var local = CreateOrder(OrderStatus.PartiallyFilled, 4m);
        var exchange = Clone(local);
        var fixture = CreateFixture(local, exchange);

        var result = await fixture.Service.CancelAsync(local.Id.ToString());

        Assert.Equal(OrderCancellationOutcome.Cancelled, result.Outcome);
        Assert.NotNull(result.Order);
        Assert.Equal(OrderStatus.Cancelled, result.Order!.Status);
        Assert.Equal(1, fixture.Exchange.CancelCalls);
        Assert.Equal("BTCUSDT", fixture.Exchange.LastSymbol);
        Assert.Equal(1, fixture.Orders.UpdateCalls);
        Assert.Single(fixture.Alerts.Alerts);
    }

    [Fact]
    public async Task CancelAsync_AlreadyCancelled_IsIdempotentWithoutExchangeCall()
    {
        var local = CreateOrder(OrderStatus.Cancelled, 4m);
        var fixture = CreateFixture(local, Clone(local));

        var result = await fixture.Service.CancelAsync(local.ClientOrderId);

        Assert.Equal(OrderCancellationOutcome.AlreadyCancelled, result.Outcome);
        Assert.Equal(0, fixture.Exchange.CancelCalls);
        Assert.Equal(0, fixture.Orders.UpdateCalls);
        Assert.Empty(fixture.Alerts.Alerts);
    }

    [Fact]
    public async Task CancelAsync_FilledOrder_ReturnsNotCancellableWithoutExchangeCall()
    {
        var local = CreateOrder(OrderStatus.Filled, 10m);
        var fixture = CreateFixture(local, Clone(local));

        var result = await fixture.Service.CancelAsync(local.ClientOrderId);

        Assert.Equal(OrderCancellationOutcome.NotCancellable, result.Outcome);
        Assert.Equal(0, fixture.Exchange.CancelCalls);
        Assert.Equal(0, fixture.Orders.UpdateCalls);
    }

    [Fact]
    public async Task CancelAsync_AmbiguousExchangeFailure_ReconcilesCancelledStateWithoutBlindRetry()
    {
        var local = CreateOrder(OrderStatus.Accepted, 0m);
        var exchange = Clone(local);
        var fixture = CreateFixture(local, exchange);
        fixture.Exchange.ThrowAfterCancelling = true;

        var result = await fixture.Service.CancelAsync(local.ClientOrderId);

        Assert.Equal(OrderCancellationOutcome.Cancelled, result.Outcome);
        Assert.Equal(OrderStatus.Cancelled, result.Order!.Status);
        Assert.Equal(1, fixture.Exchange.CancelCalls);
        Assert.Equal(1, fixture.Orders.UpdateCalls);
    }

    [Fact]
    public async Task CancelAsync_CreatedOrder_CancelsLocallyBeforeExchangeSubmission()
    {
        var local = CreateOrder(OrderStatus.Created, 0m, exchangeOrderId: null);
        var fixture = CreateFixture(local, null);

        var result = await fixture.Service.CancelAsync(local.Id.ToString());

        Assert.Equal(OrderCancellationOutcome.Cancelled, result.Outcome);
        Assert.Equal(OrderStatus.Cancelled, result.Order!.Status);
        Assert.Equal(0, fixture.Exchange.CancelCalls);
        Assert.Equal(1, fixture.Orders.UpdateCalls);
    }

    private static TestFixture CreateFixture(Order localOrder, Order? exchangeOrder)
    {
        var exchange = new TestExchangeClient(exchangeOrder);
        var reads = new TestOperatorReadRepository(localOrder);
        var orders = new TestOrderRepository();
        var alerts = new TestAlertService();
        var service = new OrderCancellationService(
            exchange,
            reads,
            orders,
            new OrderStateMachine(),
            alerts,
            NullLogger<OrderCancellationService>.Instance);

        return new TestFixture(service, exchange, orders, alerts);
    }

    private static Order CreateOrder(
        OrderStatus status,
        decimal filledQuantity,
        string? exchangeOrderId = "ex-1")
    {
        var now = DateTimeOffset.UtcNow;
        return new Order
        {
            Id = Guid.NewGuid(),
            ExchangeOrderId = exchangeOrderId,
            ClientOrderId = "client-1",
            Symbol = "BTCUSDT",
            Side = OrderSide.Buy,
            OrderType = OrderType.Limit,
            RequestedQuantity = 10m,
            FilledQuantity = filledQuantity,
            AverageFillPrice = filledQuantity > 0m ? 100m : null,
            Price = 100m,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static Order Clone(Order source)
    {
        return new Order
        {
            Id = Guid.NewGuid(),
            ExchangeOrderId = source.ExchangeOrderId,
            ClientOrderId = source.ClientOrderId,
            Symbol = source.Symbol,
            Side = source.Side,
            OrderType = source.OrderType,
            RequestedQuantity = source.RequestedQuantity,
            FilledQuantity = source.FilledQuantity,
            AverageFillPrice = source.AverageFillPrice,
            Price = source.Price,
            Status = source.Status,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt
        };
    }

    private sealed record TestFixture(
        OrderCancellationService Service,
        TestExchangeClient Exchange,
        TestOrderRepository Orders,
        TestAlertService Alerts);

    private sealed class TestExchangeClient(Order? remoteOrder) : IExchangeClient
    {
        public int CancelCalls { get; private set; }
        public string? LastSymbol { get; private set; }
        public bool ThrowAfterCancelling { get; set; }

        public Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<Position>> GetPositionsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<OrderResult> PlaceOrderAsync(
            PlaceOrderRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelOrderAsync(
            string exchangeOrderId,
            string? symbol,
            CancellationToken cancellationToken = default)
        {
            LastSymbol = symbol;
            return CancelOrderAsync(exchangeOrderId, cancellationToken);
        }

        public Task<Order?> GetOrderAsync(
            string exchangeOrderId,
            string? symbol,
            CancellationToken cancellationToken = default)
        {
            LastSymbol = symbol;
            return GetOrderAsync(exchangeOrderId, cancellationToken);
        }

        public Task<Order?> GetOrderByClientOrderIdAsync(
            string clientOrderId,
            string? symbol,
            CancellationToken cancellationToken = default)
        {
            LastSymbol = symbol;
            return GetOrderByClientOrderIdAsync(clientOrderId, cancellationToken);
        }

        public Task CancelOrderAsync(
            string exchangeOrderId,
            CancellationToken cancellationToken = default)
        {
            CancelCalls++;
            if (remoteOrder is null || remoteOrder.ExchangeOrderId != exchangeOrderId)
            {
                throw new KeyNotFoundException(exchangeOrderId);
            }

            remoteOrder.Status = OrderStatus.Cancelled;
            remoteOrder.UpdatedAt = DateTimeOffset.UtcNow;

            if (ThrowAfterCancelling)
            {
                throw new TimeoutException("Ambiguous cancellation response.");
            }

            return Task.CompletedTask;
        }

        public Task<Order?> GetOrderAsync(
            string exchangeOrderId,
            CancellationToken cancellationToken = default)
        {
            var result = remoteOrder?.ExchangeOrderId == exchangeOrderId ? remoteOrder : null;
            return Task.FromResult(result);
        }

        public Task<Order?> GetOrderByClientOrderIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default)
        {
            var result = remoteOrder?.ClientOrderId == clientOrderId ? remoteOrder : null;
            return Task.FromResult(result);
        }
    }

    private sealed class TestOperatorReadRepository(Order localOrder) : IOperatorReadRepository
    {
        public Task<TradingSignal?> GetSignalByIdAsync(
            Guid signalId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<TradingSignal>> GetSignalsAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Order?> GetLocalOrderAsync(
            string idOrClientOrderId,
            CancellationToken cancellationToken = default)
        {
            var matches = idOrClientOrderId == localOrder.Id.ToString()
                || idOrClientOrderId == localOrder.ClientOrderId;
            return Task.FromResult(matches ? localOrder : null);
        }

        public Task<IReadOnlyCollection<OrderLifecycleEvent>> GetOrderHistoryAsync(
            string idOrClientOrderId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<FillAuditRecord>> GetFillsAsync(
            string? symbol,
            int limit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestOrderRepository : IOrderRepository
    {
        public int UpdateCalls { get; private set; }

        public Task<Order?> GetByClientOrderIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<Order>> GetReconciliationCandidatesAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> TryAddAsync(
            Order order,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateAsync(
            Order order,
            CancellationToken cancellationToken = default)
        {
            UpdateCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class TestAlertService : IAlertService
    {
        public List<AlertMessage> Alerts { get; } = [];

        public Task SendAsync(
            AlertMessage alert,
            CancellationToken cancellationToken = default)
        {
            Alerts.Add(alert);
            return Task.CompletedTask;
        }
    }
}
