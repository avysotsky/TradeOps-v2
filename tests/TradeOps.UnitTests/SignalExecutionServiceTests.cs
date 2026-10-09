using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class SignalExecutionServiceTests
{
    [Fact]
    public async Task FirstSignal_IsPersisted_AndAcceptedSignalLinksLocalOrder()
    {
        var signalId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var generator = new ClientOrderIdGenerator();
        var clientOrderId = generator.Generate(signalId);
        var orderRepository = new FakeOrderRepository();

        var localOrder = CreateOrder(
            Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
            clientOrderId,
            OrderStatus.PartiallyFilled);

        var orderManager = new FakeOrderManager(signal =>
        {
            orderRepository.AddExisting(localOrder);

            return new SignalExecutionResult(
                signal.Id,
                true,
                Array.Empty<string>(),
                new OrderResult(
                    localOrder.ExchangeOrderId,
                    localOrder.ClientOrderId,
                    localOrder.Status,
                    localOrder.FilledQuantity,
                    localOrder.AverageFillPrice));
        });

        var signalRepository = new FakeTradingSignalRepository();
        var service = CreateService(
            signalRepository,
            orderRepository,
            orderManager,
            generator);

        var signal = CreateSignal(signalId, source: "unit-test");

        var result = await service.ExecuteSignalAsync(signal);

        Assert.True(result.Accepted);
        Assert.Equal(1, signalRepository.Count);

        var persisted = signalRepository.Find(signalId);
        Assert.NotNull(persisted);
        Assert.Equal(SignalOutcome.Accepted, persisted!.Outcome);
        Assert.Equal(localOrder.Id, persisted.OrderId);
        Assert.Equal(clientOrderId, persisted.ClientOrderId);
        Assert.Empty(persisted.RiskRejectionReasons);
        Assert.Equal("unit-test", persisted.Source);
        Assert.Equal(1, orderManager.CallCount);
    }

    [Fact]
    public async Task RejectedSignal_PersistsRiskReasons()
    {
        var signalId = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var reasons = new[]
        {
            "Emergency stop is active.",
            "Trading is disabled by persistent operational state."
        };

        var signalRepository = new FakeTradingSignalRepository();
        var orderRepository = new FakeOrderRepository();
        var orderManager = new FakeOrderManager(signal =>
            new SignalExecutionResult(
                signal.Id,
                false,
                reasons,
                null));

        var service = CreateService(
            signalRepository,
            orderRepository,
            orderManager,
            new ClientOrderIdGenerator());

        var result = await service.ExecuteSignalAsync(
            CreateSignal(signalId));

        Assert.False(result.Accepted);

        var persisted = signalRepository.Find(signalId);
        Assert.NotNull(persisted);
        Assert.Equal(SignalOutcome.Rejected, persisted!.Outcome);
        Assert.Equal(reasons, persisted.RiskRejectionReasons);
        Assert.Null(persisted.OrderId);
        Assert.Null(persisted.ClientOrderId);
    }

    [Fact]
    public async Task SameSignalIdRetry_ReusesPersistedAcceptedExecution()
    {
        var signalId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var generator = new ClientOrderIdGenerator();
        var clientOrderId = generator.Generate(signalId);
        var localOrder = CreateOrder(
            Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
            clientOrderId,
            OrderStatus.Filled);

        var persistedSignal = CreateSignal(signalId, source: "original");
        persistedSignal.Outcome = SignalOutcome.Accepted;
        persistedSignal.OrderId = localOrder.Id;
        persistedSignal.ClientOrderId = clientOrderId;

        var signalRepository = new FakeTradingSignalRepository(persistedSignal);
        var orderRepository = new FakeOrderRepository(localOrder);
        var orderManager = new FakeOrderManager(_ =>
            throw new InvalidOperationException("OrderManager must not run for a completed idempotent retry."));

        var service = CreateService(
            signalRepository,
            orderRepository,
            orderManager,
            generator);

        var retry = CreateSignal(signalId, source: "retry-with-different-source");

        var result = await service.ExecuteSignalAsync(retry);

        Assert.True(result.Accepted);
        Assert.Equal(OrderStatus.Filled, result.Order?.Status);
        Assert.Equal(clientOrderId, result.Order?.ClientOrderId);
        Assert.Equal(1, signalRepository.Count);
        Assert.Equal(0, orderManager.CallCount);
        Assert.Equal("original", signalRepository.Find(signalId)?.Source);
    }


    [Theory]
    [InlineData("Symbol")]
    [InlineData("Side")]
    [InlineData("SignalType")]
    [InlineData("RequestedQuantity")]
    [InlineData("RiskPercent")]
    [InlineData("StopLoss")]
    [InlineData("TakeProfit")]
    public async Task SameSignalIdRetry_WithConflictingExecutionPayload_ThrowsConflictWithoutOrderExecution(
        string conflictingField)
    {
        var signalId = Guid.Parse("66666666-6666-4666-8666-666666666666");
        var generator = new ClientOrderIdGenerator();
        var clientOrderId = generator.Generate(signalId);
        var localOrder = CreateOrder(
            Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"),
            clientOrderId,
            OrderStatus.Accepted);

        var persistedSignal = CreateSignal(signalId, source: "original");
        persistedSignal.Outcome = SignalOutcome.Accepted;
        persistedSignal.OrderId = localOrder.Id;
        persistedSignal.ClientOrderId = clientOrderId;

        var signalRepository = new FakeTradingSignalRepository(persistedSignal);
        var orderRepository = new FakeOrderRepository(localOrder);
        var orderManager = new FakeOrderManager(_ =>
            throw new InvalidOperationException("OrderManager must not run for a conflicting SignalId retry."));

        var service = CreateService(
            signalRepository,
            orderRepository,
            orderManager,
            generator);

        var retry = CreateSignal(signalId, source: "retry-metadata");
        switch (conflictingField)
        {
            case "Symbol":
                retry.Symbol = "ETHUSDT";
                break;
            case "Side":
                retry.Side = OrderSide.Sell;
                break;
            case "SignalType":
                retry.SignalType = "DifferentExternalType";
                break;
            case "RequestedQuantity":
                retry.RequestedQuantity = 0.002m;
                break;
            case "RiskPercent":
                retry.RiskPercent = 1m;
                break;
            case "StopLoss":
                retry.StopLoss = 49000m;
                break;
            case "TakeProfit":
                retry.TakeProfit = 51000m;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(conflictingField));
        }

        var exception = await Assert.ThrowsAsync<SignalIdConflictException>(
            () => service.ExecuteSignalAsync(retry));

        Assert.Equal(signalId, exception.SignalId);
        Assert.Contains(conflictingField, exception.ConflictingFields);
        Assert.Equal(0, orderManager.CallCount);
        Assert.Equal(1, signalRepository.Count);
        Assert.Equal("original", signalRepository.Find(signalId)?.Source);
    }


    [Fact]
    public async Task AcceptedSignalRetry_WithConflictingLinkedOrder_ThrowsClientOrderIdConflict()
    {
        var signalId = Guid.Parse("98989898-9898-4898-8898-989898989898");
        var generator = new ClientOrderIdGenerator();
        var clientOrderId = generator.Generate(signalId);
        var localOrder = CreateOrder(
            Guid.Parse("97979797-9797-4797-8797-979797979797"),
            clientOrderId,
            OrderStatus.Accepted);
        localOrder.RequestedQuantity = 0.002m;

        var persistedSignal = CreateSignal(signalId, source: "original");
        persistedSignal.Outcome = SignalOutcome.Accepted;
        persistedSignal.OrderId = localOrder.Id;
        persistedSignal.ClientOrderId = clientOrderId;

        var signalRepository = new FakeTradingSignalRepository(persistedSignal);
        var orderRepository = new FakeOrderRepository(localOrder);
        var orderManager = new FakeOrderManager(_ =>
            throw new InvalidOperationException("OrderManager must not run for an Accepted retry."));

        var service = CreateService(
            signalRepository,
            orderRepository,
            orderManager,
            generator);

        var exception = await Assert.ThrowsAsync<ClientOrderIdConflictException>(
            () => service.ExecuteSignalAsync(CreateSignal(signalId, source: "retry")));

        Assert.Contains(nameof(Order.RequestedQuantity), exception.ConflictingFields);
        Assert.Equal(0, orderManager.CallCount);

        var afterConflict = signalRepository.Find(signalId);
        Assert.Equal("ClientOrderIdConflict", afterConflict?.ExecutionIssueCode);
        Assert.NotNull(afterConflict?.ExecutionIssueMessage);
        Assert.NotNull(afterConflict?.ExecutionIssueAt);
        Assert.Equal(SignalOutcome.Accepted, afterConflict?.Outcome);

        localOrder.RequestedQuantity = persistedSignal.RequestedQuantity;

        var recovered = await service.ExecuteSignalAsync(
            CreateSignal(signalId, source: "recovered-retry"));

        Assert.True(recovered.Accepted);
        var afterRecovery = signalRepository.Find(signalId);
        Assert.Null(afterRecovery?.ExecutionIssueCode);
        Assert.Null(afterRecovery?.ExecutionIssueMessage);
        Assert.Null(afterRecovery?.ExecutionIssueAt);
        Assert.Equal(SignalOutcome.Accepted, afterRecovery?.Outcome);
    }

    [Fact]
    public async Task DuplicateInsertRace_ReusesWinnerWithoutCreatingSecondAuditRow()
    {
        var signalId = Guid.Parse("44444444-4444-4444-8444-444444444444");
        var generator = new ClientOrderIdGenerator();
        var clientOrderId = generator.Generate(signalId);
        var localOrder = CreateOrder(
            Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"),
            clientOrderId,
            OrderStatus.Accepted);

        var raceWinner = CreateSignal(signalId, source: "race-winner");
        raceWinner.Outcome = SignalOutcome.Accepted;
        raceWinner.OrderId = localOrder.Id;
        raceWinner.ClientOrderId = clientOrderId;

        var signalRepository = new FakeTradingSignalRepository
        {
            DuplicateWinnerOnNextInsert = raceWinner
        };

        var orderRepository = new FakeOrderRepository(localOrder);
        var orderManager = new FakeOrderManager(_ =>
            throw new InvalidOperationException("OrderManager must not run after duplicate persistence is resolved."));

        var service = CreateService(
            signalRepository,
            orderRepository,
            orderManager,
            generator);

        var result = await service.ExecuteSignalAsync(
            CreateSignal(signalId, source: "race-loser"));

        Assert.True(result.Accepted);
        Assert.Equal(1, signalRepository.Count);
        Assert.Equal("race-winner", signalRepository.Find(signalId)?.Source);
        Assert.Equal(0, orderManager.CallCount);
    }


    [Fact]
    public async Task DuplicateInsertRace_WithConflictingWinner_ThrowsConflictWithoutOrderExecution()
    {
        var signalId = Guid.Parse("77777777-7777-4777-8777-777777777777");
        var raceWinner = CreateSignal(signalId, source: "race-winner");
        raceWinner.RequestedQuantity = 0.002m;
        raceWinner.Outcome = SignalOutcome.Received;

        var signalRepository = new FakeTradingSignalRepository
        {
            DuplicateWinnerOnNextInsert = raceWinner
        };
        var orderRepository = new FakeOrderRepository();
        var orderManager = new FakeOrderManager(_ =>
            throw new InvalidOperationException("OrderManager must not run after a conflicting duplicate insert race."));

        var service = CreateService(
            signalRepository,
            orderRepository,
            orderManager,
            new ClientOrderIdGenerator());

        var incoming = CreateSignal(signalId, source: "race-loser");

        var exception = await Assert.ThrowsAsync<SignalIdConflictException>(
            () => service.ExecuteSignalAsync(incoming));

        Assert.Contains(nameof(TradingSignal.RequestedQuantity), exception.ConflictingFields);
        Assert.Equal(0, orderManager.CallCount);
        Assert.Equal(1, signalRepository.Count);
        Assert.Equal(0.002m, signalRepository.Find(signalId)?.RequestedQuantity);
    }

    [Fact]
    public async Task SameRejectedSignalIdRetry_ReturnsPersistedRejectionWithoutRunningRiskAgain()
    {
        var signalId = Guid.Parse("55555555-5555-4555-8555-555555555555");
        var persistedSignal = CreateSignal(signalId);
        persistedSignal.Outcome = SignalOutcome.Rejected;
        persistedSignal.RiskRejectionReasons =
        [
            "Projected position exceeds max position size."
        ];

        var signalRepository = new FakeTradingSignalRepository(persistedSignal);
        var orderRepository = new FakeOrderRepository();
        var orderManager = new FakeOrderManager(_ =>
            throw new InvalidOperationException("OrderManager must not run for a completed rejected retry."));

        var service = CreateService(
            signalRepository,
            orderRepository,
            orderManager,
            new ClientOrderIdGenerator());

        var result = await service.ExecuteSignalAsync(
            CreateSignal(signalId));

        Assert.False(result.Accepted);
        Assert.Equal(persistedSignal.RiskRejectionReasons, result.RiskReasons);
        Assert.Null(result.Order);
        Assert.Equal(0, orderManager.CallCount);
    }

    private static SignalExecutionService CreateService(
        ITradingSignalRepository signalRepository,
        IOrderRepository orderRepository,
        IOrderManager orderManager,
        IClientOrderIdGenerator generator)
    {
        return new SignalExecutionService(
            signalRepository,
            orderRepository,
            orderManager,
            generator,
            NullLogger<SignalExecutionService>.Instance);
    }

    private static TradingSignal CreateSignal(
        Guid id,
        string source = "test")
    {
        return new TradingSignal
        {
            Id = id,
            Symbol = "BTCUSDT",
            Side = OrderSide.Buy,
            SignalType = "External",
            RequestedQuantity = 0.001m,
            CreatedAt = DateTimeOffset.UtcNow,
            Source = source
        };
    }

    private static Order CreateOrder(
        Guid id,
        string clientOrderId,
        OrderStatus status)
    {
        return new Order
        {
            Id = id,
            ClientOrderId = clientOrderId,
            ExchangeOrderId = "exchange-order",
            Symbol = "BTCUSDT",
            Side = OrderSide.Buy,
            OrderType = OrderType.Market,
            RequestedQuantity = 0.001m,
            FilledQuantity = status == OrderStatus.Filled ? 0.001m : 0.0006m,
            AverageFillPrice = 50000m,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed class FakeTradingSignalRepository : ITradingSignalRepository
    {
        private readonly Dictionary<Guid, TradingSignal> _signals;
        private bool _firstLookup = true;

        public FakeTradingSignalRepository(params TradingSignal[] existing)
        {
            _signals = existing.ToDictionary(signal => signal.Id);
        }

        public TradingSignal? DuplicateWinnerOnNextInsert { get; init; }

        public int Count => _signals.Count;

        public TradingSignal? Find(Guid id) =>
            _signals.TryGetValue(id, out var signal) ? signal : null;

        public Task<TradingSignal?> GetByIdAsync(
            Guid signalId,
            CancellationToken cancellationToken = default)
        {
            if (DuplicateWinnerOnNextInsert is not null && _firstLookup)
            {
                _firstLookup = false;
                return Task.FromResult<TradingSignal?>(null);
            }

            _firstLookup = false;
            return Task.FromResult(Find(signalId));
        }

        public Task<bool> TryAddAsync(
            TradingSignal signal,
            CancellationToken cancellationToken = default)
        {
            if (DuplicateWinnerOnNextInsert is not null)
            {
                _signals[DuplicateWinnerOnNextInsert.Id] = DuplicateWinnerOnNextInsert;
                return Task.FromResult(false);
            }

            if (_signals.ContainsKey(signal.Id))
            {
                return Task.FromResult(false);
            }

            _signals[signal.Id] = signal;
            return Task.FromResult(true);
        }

        public Task UpdateAsync(
            TradingSignal signal,
            CancellationToken cancellationToken = default)
        {
            _signals[signal.Id] = signal;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOrderRepository : IOrderRepository
    {
        private readonly Dictionary<string, Order> _orders;

        public FakeOrderRepository(params Order[] existing)
        {
            _orders = existing.ToDictionary(order => order.ClientOrderId);
        }

        public void AddExisting(Order order)
        {
            _orders[order.ClientOrderId] = order;
        }

        public Task<Order?> GetByClientOrderIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default)
        {
            _orders.TryGetValue(clientOrderId, out var order);
            return Task.FromResult(order);
        }

        public Task<IReadOnlyCollection<Order>> GetReconciliationCandidatesAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyCollection<Order>>(
                Array.Empty<Order>());
        }

        public Task<bool> TryAddAsync(
            Order order,
            CancellationToken cancellationToken = default)
        {
            var inserted = _orders.TryAdd(order.ClientOrderId, order);
            return Task.FromResult(inserted);
        }

        public Task UpdateAsync(
            Order order,
            CancellationToken cancellationToken = default)
        {
            _orders[order.ClientOrderId] = order;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOrderManager : IOrderManager
    {
        private readonly Func<TradingSignal, SignalExecutionResult> _handler;

        public FakeOrderManager(
            Func<TradingSignal, SignalExecutionResult> handler)
        {
            _handler = handler;
        }

        public int CallCount { get; private set; }

        public Task<SignalExecutionResult> ExecuteSignalAsync(
            TradingSignal signal,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_handler(signal));
        }
    }
}
