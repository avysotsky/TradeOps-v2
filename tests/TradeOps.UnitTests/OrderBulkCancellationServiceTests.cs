using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class OrderBulkCancellationServiceTests
{
    [Fact]
    public async Task CancelOpenOrdersAsync_AggregatesOutcomesAndNormalizesSymbol()
    {
        var first = CreateOrder("first", "BTCUSDT");
        var second = CreateOrder("second", "BTCUSDT");
        var third = CreateOrder("third", "BTCUSDT");
        var candidates = new FakeCandidateRepository([first, second, third]);
        var cancellation = new FakeCancellationService(new Dictionary<string, OrderCancellationOutcome>
        {
            [first.ClientOrderId] = OrderCancellationOutcome.Cancelled,
            [second.ClientOrderId] = OrderCancellationOutcome.CancellationRequested,
            [third.ClientOrderId] = OrderCancellationOutcome.Unresolved
        });
        var service = new OrderBulkCancellationService(
            candidates,
            cancellation,
            NullLogger<OrderBulkCancellationService>.Instance);

        var result = await service.CancelOpenOrdersAsync(" btcusdt ");

        Assert.Equal("BTCUSDT", result.Symbol);
        Assert.Equal("BTCUSDT", candidates.LastSymbol);
        Assert.Equal(3, result.CandidateCount);
        Assert.Equal(1, result.CancelledCount);
        Assert.Equal(1, result.CancellationRequestedCount);
        Assert.Equal(1, result.UnresolvedCount);
        Assert.False(result.IsComplete);
        Assert.Equal(3, cancellation.Calls.Count);
    }

    [Fact]
    public async Task CancelOpenOrdersAsync_NoCandidates_IsCompleteAndDoesNotCallSingleCancel()
    {
        var candidates = new FakeCandidateRepository(Array.Empty<Order>());
        var cancellation = new FakeCancellationService(
            new Dictionary<string, OrderCancellationOutcome>());
        var service = new OrderBulkCancellationService(
            candidates,
            cancellation,
            NullLogger<OrderBulkCancellationService>.Instance);

        var result = await service.CancelOpenOrdersAsync();

        Assert.Null(result.Symbol);
        Assert.Equal(0, result.CandidateCount);
        Assert.True(result.IsComplete);
        Assert.Empty(cancellation.Calls);
    }

    private static Order CreateOrder(string suffix, string symbol) => new()
    {
        Id = Guid.NewGuid(),
        ExchangeOrderId = $"exchange-{suffix}",
        ClientOrderId = $"client-{suffix}",
        Symbol = symbol,
        Side = OrderSide.Buy,
        OrderType = OrderType.Market,
        RequestedQuantity = 1m,
        FilledQuantity = 0.5m,
        AverageFillPrice = 100m,
        Status = OrderStatus.PartiallyFilled,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private sealed class FakeCandidateRepository(IReadOnlyCollection<Order> orders)
        : IOrderCancellationCandidateRepository
    {
        public string? LastSymbol { get; private set; }

        public Task<IReadOnlyCollection<Order>> GetCancellationCandidatesAsync(
            string? symbol = null,
            CancellationToken cancellationToken = default)
        {
            LastSymbol = symbol;
            return Task.FromResult(orders);
        }
    }

    private sealed class FakeCancellationService(
        IReadOnlyDictionary<string, OrderCancellationOutcome> outcomes)
        : IOrderCancellationService
    {
        public List<string> Calls { get; } = [];

        public Task<OrderCancellationResult> CancelAsync(
            string idOrClientOrderId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(idOrClientOrderId);
            var outcome = outcomes[idOrClientOrderId];
            return Task.FromResult(new OrderCancellationResult(
                outcome,
                null,
                outcome.ToString()));
        }
    }
}
