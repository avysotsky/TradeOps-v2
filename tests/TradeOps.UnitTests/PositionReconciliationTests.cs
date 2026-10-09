using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class PositionReconciliationTests
{
    [Fact]
    public async Task ReconcileAsync_MismatchIsDeduplicated_AndResolvedWhenExposureMatches()
    {
        var positionService = new FakePositionService(
        [
            State("BTCUSDT", OrderSide.Buy, 1m)
        ]);
        var exchange = new FakeExchangeClient(
        [
            ExchangePosition("BTCUSDT", OrderSide.Buy, 0.5m)
        ]);
        var riskEvents = new FakeRiskEventRepository();
        var alerts = new FakeAlertService();
        var service = new PositionReconciliationService(
            positionService,
            exchange,
            riskEvents,
            alerts,
            NullLogger<PositionReconciliationService>.Instance);

        var first = await service.ReconcileAsync();
        var second = await service.ReconcileAsync();

        Assert.Equal(1, first.Mismatched);
        Assert.Equal(1, second.Mismatched);
        Assert.Single(riskEvents.Events);
        Assert.Single(alerts.Alerts);
        Assert.Equal(AlertSeverity.Warning, alerts.Alerts[0].Severity);
        Assert.Null(riskEvents.Events[0].ResolvedAt);

        exchange.Positions =
        [
            ExchangePosition("BTCUSDT", OrderSide.Buy, 1m)
        ];

        var resolved = await service.ReconcileAsync();

        Assert.Equal(1, resolved.Matched);
        Assert.Equal(0, resolved.Mismatched);
        Assert.NotNull(riskEvents.Events[0].ResolvedAt);
        Assert.Equal(2, alerts.Alerts.Count);
        Assert.Equal(AlertSeverity.Info, alerts.Alerts[1].Severity);
    }

    [Fact]
    public async Task ReconcileAsync_ExchangeOnlyExposure_CreatesMismatchEvent()
    {
        var riskEvents = new FakeRiskEventRepository();
        var service = new PositionReconciliationService(
            new FakePositionService([]),
            new FakeExchangeClient(
            [
                ExchangePosition("ETHUSDT", OrderSide.Sell, 2m)
            ]),
            riskEvents,
            new FakeAlertService(),
            NullLogger<PositionReconciliationService>.Instance);

        var summary = await service.ReconcileAsync();

        var issue = Assert.Single(summary.Issues);
        Assert.Equal("ETHUSDT", issue.Symbol);
        Assert.Null(issue.LocalSide);
        Assert.Equal(0m, issue.LocalQuantity);
        Assert.Equal(OrderSide.Sell, issue.ExchangeSide);
        Assert.Equal(2m, issue.ExchangeQuantity);
        Assert.Contains("exchange has open exposure", issue.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Single(riskEvents.Events);
    }

    [Fact]
    public async Task ReconcileAsync_PreviousMismatchBecomesFlatOnBothSides_ResolvesStaleEvent()
    {
        var riskEvents = new FakeRiskEventRepository();
        riskEvents.Events.Add(new RiskEvent
        {
            Id = Guid.NewGuid(),
            EventType = "PositionMismatch",
            EventKey = "BTCUSDT",
            Symbol = "BTCUSDT",
            Message = "previous mismatch",
            Severity = RiskEventSeverity.Warning,
            LocalSide = OrderSide.Buy,
            LocalQuantity = 1m,
            ExchangeSide = null,
            ExchangeQuantity = 0m,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            LastObservedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        var alerts = new FakeAlertService();
        var service = new PositionReconciliationService(
            new FakePositionService([]),
            new FakeExchangeClient([]),
            riskEvents,
            alerts,
            NullLogger<PositionReconciliationService>.Instance);

        var summary = await service.ReconcileAsync();

        Assert.Equal(1, summary.SymbolsCompared);
        Assert.Equal(1, summary.Matched);
        Assert.NotNull(riskEvents.Events[0].ResolvedAt);
        Assert.Single(alerts.Alerts);
        Assert.Equal(AlertSeverity.Info, alerts.Alerts[0].Severity);
    }

    private static PositionState State(string symbol, OrderSide side, decimal quantity) =>
        new(symbol, side, quantity, 100m, 0m, null, null, null, DateTimeOffset.UtcNow);

    private static Position ExchangePosition(string symbol, OrderSide side, decimal quantity) => new()
    {
        Symbol = symbol,
        Side = side,
        Quantity = quantity,
        AverageEntryPrice = 100m,
        MarkPrice = 100m,
        UnrealizedPnL = 0m
    };

    private sealed class FakePositionService(IReadOnlyCollection<PositionState> states) : IPositionService
    {
        public Task<IReadOnlyCollection<PositionState>> GetLocalAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(states);

        public Task<IReadOnlyCollection<PositionState>> GetCurrentAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(states);

        public Task<IReadOnlyCollection<PositionState>> CaptureSnapshotsAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(states);
    }

    private sealed class FakeRiskEventRepository : IRiskEventRepository
    {
        public List<RiskEvent> Events { get; } = [];

        public Task<RiskEvent?> GetActiveAsync(
            string eventType,
            string eventKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Events.FirstOrDefault(item =>
                item.EventType == eventType
                && item.EventKey == eventKey
                && item.ResolvedAt is null));

        public Task<IReadOnlyCollection<RiskEvent>> GetActiveByTypeAsync(
            string eventType,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<RiskEvent>>(Events
                .Where(item => item.EventType == eventType && item.ResolvedAt is null)
                .ToArray());

        public Task<bool> TryAddAsync(
            RiskEvent riskEvent,
            CancellationToken cancellationToken = default)
        {
            if (Events.Any(item => item.EventType == riskEvent.EventType
                && item.EventKey == riskEvent.EventKey
                && item.ResolvedAt is null))
            {
                return Task.FromResult(false);
            }

            Events.Add(riskEvent);
            return Task.FromResult(true);
        }

        public Task UpdateAsync(
            RiskEvent riskEvent,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyCollection<RiskEvent>> GetRecentAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<RiskEvent>>(Events.Take(limit).ToArray());
    }

    private sealed class FakeAlertService : IAlertService
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

    private sealed class FakeExchangeClient(IReadOnlyCollection<Position> positions) : IExchangeClient
    {
        public IReadOnlyCollection<Position> Positions { get; set; } = positions;

        public Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<Position>> GetPositionsAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(Positions);

        public Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<OrderResult> PlaceOrderAsync(
            PlaceOrderRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CancelOrderAsync(
            string exchangeOrderId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Order?> GetOrderAsync(
            string exchangeOrderId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Order?> GetOrderByClientOrderIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
