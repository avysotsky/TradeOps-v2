using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class ExchangeEventFillPersistenceTests
{
    [Fact]
    public async Task ProcessExecutionUpdateAsync_DuplicateExecution_PersistsSingleFill()
    {
        var order = CreateOrder();
        var orderRepository = new FakeOrderRepository(order);
        var fillRepository = new FakeFillRepository();
        var processor = new ExchangeEventProcessor(
            orderRepository,
            fillRepository,
            new OrderStateMachine(),
            NullLogger<ExchangeEventProcessor>.Instance);

        var executedAt = DateTimeOffset.Parse("2026-10-01T06:30:00Z");
        var update = new ExchangeExecutionUpdate(
            order.ExchangeOrderId!,
            order.ClientOrderId,
            "exec-001",
            order.Symbol,
            order.Side,
            0.001m,
            65000m,
            0.00001m,
            "USDT",
            executedAt);

        await processor.ProcessExecutionUpdateAsync(update);
        await processor.ProcessExecutionUpdateAsync(update);

        var fill = Assert.Single(fillRepository.Fills);
        Assert.Equal(order.Id, fill.OrderId);
        Assert.Equal("exec-001", fill.ExchangeFillId);
        Assert.Equal(0.001m, fill.Quantity);
        Assert.Equal(65000m, fill.Price);
        Assert.Equal(0.00001m, fill.Fee);
        Assert.Equal("USDT", fill.FeeCurrency);
        Assert.Equal(executedAt, fill.FilledAt);
    }

    [Fact]
    public async Task ProcessExecutionUpdateAsync_WithoutClientOrderId_ResolvesByExchangeOrderId()
    {
        var order = CreateOrder();
        var orderRepository = new FakeOrderRepository(order);
        var fillRepository = new FakeFillRepository();
        var processor = new ExchangeEventProcessor(
            orderRepository,
            fillRepository,
            new OrderStateMachine(),
            NullLogger<ExchangeEventProcessor>.Instance);

        await processor.ProcessExecutionUpdateAsync(
            new ExchangeExecutionUpdate(
                order.ExchangeOrderId!,
                string.Empty,
                "hl:1760000002000:BTC:55555",
                order.Symbol,
                order.Side,
                0.001m,
                65000m,
                0.00001m,
                "USDC",
                DateTimeOffset.Parse("2026-10-05T18:00:00Z")));

        var fill = Assert.Single(fillRepository.Fills);
        Assert.Equal(order.Id, fill.OrderId);
        Assert.Equal("hl:1760000002000:BTC:55555", fill.ExchangeFillId);
    }

    [Fact]
    public async Task ProcessExecutionUpdateAsync_IdentityMismatch_DoesNotPersistFill()
    {
        var order = CreateOrder();
        var fillRepository = new FakeFillRepository();
        var processor = new ExchangeEventProcessor(
            new FakeOrderRepository(order),
            fillRepository,
            new OrderStateMachine(),
            NullLogger<ExchangeEventProcessor>.Instance);

        await processor.ProcessExecutionUpdateAsync(new ExchangeExecutionUpdate(
            order.ExchangeOrderId!,
            order.ClientOrderId,
            "exec-002",
            "ETHUSDT",
            order.Side,
            0.001m,
            3500m,
            null,
            null,
            DateTimeOffset.UtcNow));

        Assert.Empty(fillRepository.Fills);
    }

    private static Order CreateOrder() => new()
    {
        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        ExchangeOrderId = "exchange-order-001",
        ClientOrderId = "trd-d0f8625f2ad444eba1ec22acbfbb2e58",
        Symbol = "BTCUSDT",
        Side = OrderSide.Buy,
        OrderType = OrderType.Market,
        RequestedQuantity = 0.01m,
        FilledQuantity = 0m,
        Status = OrderStatus.Accepted,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private sealed class FakeFillRepository : IFillRepository
    {
        private readonly HashSet<string> _executionIds = new(StringComparer.Ordinal);

        public List<Fill> Fills { get; } = [];

        public Task<bool> TryAddAsync(Fill fill, CancellationToken cancellationToken = default)
        {
            if (!_executionIds.Add(fill.ExchangeFillId))
            {
                return Task.FromResult(false);
            }

            Fills.Add(fill);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyCollection<PositionFill>> GetPositionFillsAsync(
            string? symbol = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<PositionFill>>(Array.Empty<PositionFill>());
    }

    private sealed class FakeOrderRepository(Order order) : IOrderRepository
    {
        public Task<Order?> GetByClientOrderIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Order?>(string.Equals(order.ClientOrderId, clientOrderId, StringComparison.Ordinal)
                ? order
                : null);

        public Task<Order?> GetByExchangeOrderIdAsync(
            string exchangeOrderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Order?>(
                string.Equals(
                    order.ExchangeOrderId,
                    exchangeOrderId,
                    StringComparison.Ordinal)
                    ? order
                    : null);

        public Task<IReadOnlyCollection<Order>> GetReconciliationCandidatesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Order>>(Array.Empty<Order>());

        public Task<bool> TryAddAsync(
            Order candidate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task UpdateAsync(
            Order candidate,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
