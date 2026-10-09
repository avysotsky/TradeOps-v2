using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class OrderRecoveryTests
{
    [Fact]
    public async Task ExecuteSignalAsync_TimeoutThenExchangeLookupFindsOrder_DoesNotRetryPlacement()
    {
        var signal = CreateSignal();
        var repository = new InMemoryOrderRepository();
        var exchange = new RecoveryExchangeClient
        {
            PlaceOrderException = new TimeoutException("ambiguous placement"),
            LookupByClientOrderIdResult = CreateExchangeOrder(signal, OrderStatus.Filled)
        };
        var manager = CreateOrderManager(exchange, repository);

        var result = await manager.ExecuteSignalAsync(signal);

        Assert.True(result.Accepted);
        Assert.NotNull(result.Order);
        Assert.Equal(OrderStatus.Filled, result.Order.Status);
        Assert.Equal(1, exchange.PlaceOrderCallCount);
        Assert.Equal(1, exchange.LookupByClientOrderIdCallCount);

        var stored = Assert.Single(repository.Orders);
        Assert.Equal(OrderStatus.Filled, stored.Status);
        Assert.Equal("exchange-recovered", stored.ExchangeOrderId);
        Assert.Equal(signal.RequestedQuantity, stored.FilledQuantity);
    }

    [Fact]
    public async Task ExecuteSignalAsync_TimeoutAndLookupMiss_MarksUnknown_ThenReconciliationRecoversFilled()
    {
        var signal = CreateSignal();
        var repository = new InMemoryOrderRepository();
        var exchange = new RecoveryExchangeClient
        {
            PlaceOrderException = new TimeoutException("ambiguous placement"),
            LookupByClientOrderIdResult = null
        };
        var manager = CreateOrderManager(exchange, repository);

        var initial = await manager.ExecuteSignalAsync(signal);

        Assert.True(initial.Accepted);
        Assert.NotNull(initial.Order);
        Assert.Equal(OrderStatus.Unknown, initial.Order.Status);
        Assert.Equal(1, exchange.PlaceOrderCallCount);
        Assert.Equal(1, exchange.LookupByClientOrderIdCallCount);

        var stored = Assert.Single(repository.Orders);
        Assert.Equal(OrderStatus.Unknown, stored.Status);
        Assert.Null(stored.ExchangeOrderId);

        exchange.LookupByClientOrderIdResult = CreateExchangeOrder(signal, OrderStatus.Filled);
        var runStatus = new InMemoryOperationalRunStatusRepository();
        var reconciliation = CreateReconciliationService(exchange, repository, runStatus);

        var summary = await reconciliation.ReconcileAsync();

        Assert.Equal(1, summary.Scanned);
        Assert.Equal(1, summary.Updated);
        Assert.Equal(0, summary.MissingOnExchange);
        Assert.Empty(summary.Issues);
        Assert.Equal(OrderStatus.Filled, stored.Status);
        Assert.Equal("exchange-recovered", stored.ExchangeOrderId);
        Assert.Equal(signal.RequestedQuantity, stored.FilledQuantity);
        Assert.Equal(2, exchange.LookupByClientOrderIdCallCount);
        Assert.Equal(1, exchange.PlaceOrderCallCount);

        var status = await runStatus.GetAsync(OperationalRunTypes.OrderReconciliation);
        Assert.NotNull(status);
        Assert.False(status.IsRunning);
        Assert.True(status.Succeeded);
        Assert.Equal(1, status.OrdersScanned);
        Assert.Equal(1, status.OrdersUpdated);
        Assert.Equal(0, status.OrderIssues);
        Assert.Equal(0, status.OrdersMissingOnExchange);
    }

    [Fact]
    public async Task ReconcileAsync_UnknownOrderStillMissing_RemainsUnknownAndReportsIssue()
    {
        var signal = CreateSignal();
        var repository = new InMemoryOrderRepository();
        var order = new Order
        {
            Id = Guid.NewGuid(),
            ClientOrderId = new ClientOrderIdGenerator().Generate(signal.Id),
            Symbol = signal.Symbol,
            Side = signal.Side,
            OrderType = OrderType.Market,
            RequestedQuantity = signal.RequestedQuantity,
            FilledQuantity = 0m,
            Status = OrderStatus.Unknown,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        repository.Orders.Add(order);

        var exchange = new RecoveryExchangeClient();
        var runStatus = new InMemoryOperationalRunStatusRepository();
        var reconciliation = CreateReconciliationService(exchange, repository, runStatus);

        var summary = await reconciliation.ReconcileAsync();

        Assert.Equal(OrderStatus.Unknown, order.Status);
        Assert.Equal(1, summary.Scanned);
        Assert.Equal(0, summary.Updated);
        Assert.Equal(1, summary.Unchanged);
        Assert.Equal(1, summary.MissingOnExchange);
        Assert.Single(summary.Issues);
        Assert.Equal(1, exchange.LookupByClientOrderIdCallCount);
        Assert.Equal(0, exchange.PlaceOrderCallCount);

        var status = await runStatus.GetAsync(OperationalRunTypes.OrderReconciliation);
        Assert.NotNull(status);
        Assert.True(status.Succeeded);
        Assert.Equal(1, status.OrdersScanned);
        Assert.Equal(0, status.OrdersUpdated);
        Assert.Equal(1, status.OrderIssues);
        Assert.Equal(1, status.OrdersMissingOnExchange);
    }

    private static OrderReconciliationService CreateReconciliationService(
        IExchangeClient exchangeClient,
        IOrderRepository repository,
        IOperationalRunStatusRepository runStatusRepository) =>
        new(
            exchangeClient,
            repository,
            new OrderStateMachine(),
            new NoOpAlertService(),
            runStatusRepository,
            NullLogger<OrderReconciliationService>.Instance);

    private static OrderManager CreateOrderManager(
        IExchangeClient exchangeClient,
        IOrderRepository repository) =>
        new(
            new AllowAllRiskEngine(),
            exchangeClient,
            repository,
            new ClientOrderIdGenerator(),
            new OrderStateMachine(),
            new NoOpAlertService(),
            NullLogger<OrderManager>.Instance);

    private static TradingSignal CreateSignal() => new()
    {
        Id = Guid.Parse("d0f8625f-2ad4-44eb-a1ec-22acbfbb2e58"),
        Symbol = "BTCUSDT",
        Side = OrderSide.Buy,
        SignalType = "External",
        RequestedQuantity = 0.001m,
        CreatedAt = DateTimeOffset.UtcNow,
        Source = "unit-test"
    };

    private static Order CreateExchangeOrder(TradingSignal signal, OrderStatus status) => new()
    {
        Id = Guid.NewGuid(),
        ExchangeOrderId = "exchange-recovered",
        ClientOrderId = new ClientOrderIdGenerator().Generate(signal.Id),
        Symbol = signal.Symbol,
        Side = signal.Side,
        OrderType = OrderType.Market,
        RequestedQuantity = signal.RequestedQuantity,
        FilledQuantity = status == OrderStatus.Filled ? signal.RequestedQuantity : 0m,
        AverageFillPrice = status == OrderStatus.Filled ? 64_100m : null,
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private sealed class AllowAllRiskEngine : IRiskEngine
    {
        public Task<RiskDecision> CheckAsync(
            TradingSignal signal,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RiskDecision.Allowed());
    }

    private sealed class NoOpAlertService : IAlertService
    {
        public Task SendAsync(
            AlertMessage alert,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class InMemoryOperationalRunStatusRepository : IOperationalRunStatusRepository
    {
        private readonly Dictionary<string, OperationalRunStatus> _statuses = new();

        public Task<OperationalRunStatus?> GetAsync(
            string runType,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_statuses.GetValueOrDefault(runType));

        public Task MarkStartedAsync(
            string runType,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            _statuses[runType] = new OperationalRunStatus
            {
                RunType = runType,
                StartedAt = now,
                IsRunning = true,
                UpdatedAt = now
            };
            return Task.CompletedTask;
        }

        public Task MarkCompletedAsync(
            string runType,
            OperationalRunMetrics metrics,
            CancellationToken cancellationToken = default)
        {
            var status = _statuses[runType];
            status.CompletedAt = DateTimeOffset.UtcNow;
            status.IsRunning = false;
            status.Succeeded = true;
            status.OrdersScanned = metrics.OrdersScanned;
            status.OrdersUpdated = metrics.OrdersUpdated;
            status.OrderIssues = metrics.OrderIssues;
            status.OrdersMissingOnExchange = metrics.OrdersMissingOnExchange;
            status.PositionsCompared = metrics.PositionsCompared;
            status.PositionMismatches = metrics.PositionMismatches;
            status.PositionSnapshots = metrics.PositionSnapshots;
            status.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(
            string runType,
            string errorMessage,
            CancellationToken cancellationToken = default)
        {
            var status = _statuses[runType];
            status.CompletedAt = DateTimeOffset.UtcNow;
            status.IsRunning = false;
            status.Succeeded = false;
            status.ErrorMessage = errorMessage;
            status.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryOrderRepository : IOrderRepository
    {
        public List<Order> Orders { get; } = [];

        public Task<Order?> GetByClientOrderIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Orders.FirstOrDefault(order => order.ClientOrderId == clientOrderId));

        public Task<IReadOnlyCollection<Order>> GetReconciliationCandidatesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Order>>(
                Orders.Where(order => order.Status is not OrderStatus.Filled
                    and not OrderStatus.Cancelled
                    and not OrderStatus.Rejected).ToArray());

        public Task<bool> TryAddAsync(
            Order order,
            CancellationToken cancellationToken = default)
        {
            if (Orders.Any(existing => existing.ClientOrderId == order.ClientOrderId))
            {
                return Task.FromResult(false);
            }

            Orders.Add(order);
            return Task.FromResult(true);
        }

        public Task UpdateAsync(
            Order order,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecoveryExchangeClient : IExchangeClient
    {
        public Exception? PlaceOrderException { get; init; }
        public Order? LookupByClientOrderIdResult { get; set; }
        public int PlaceOrderCallCount { get; private set; }
        public int LookupByClientOrderIdCallCount { get; private set; }

        public Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<Position>> GetPositionsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Position>>(Array.Empty<Position>());

        public Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Order>>(Array.Empty<Order>());

        public Task<OrderResult> PlaceOrderAsync(
            PlaceOrderRequest request,
            CancellationToken cancellationToken = default)
        {
            PlaceOrderCallCount++;

            if (PlaceOrderException is not null)
            {
                throw PlaceOrderException;
            }

            throw new InvalidOperationException("Test exchange expected ambiguous placement.");
        }

        public Task CancelOrderAsync(
            string exchangeOrderId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Order?> GetOrderAsync(
            string exchangeOrderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Order?>(null);

        public Task<Order?> GetOrderByClientOrderIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default)
        {
            LookupByClientOrderIdCallCount++;
            return Task.FromResult(LookupByClientOrderIdResult);
        }
    }
}
