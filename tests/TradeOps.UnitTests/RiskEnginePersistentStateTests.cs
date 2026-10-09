using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class RiskEnginePersistentStateTests
{
    [Fact]
    public async Task CheckAsync_ActivePositionMismatch_BlocksTradingAndRecordsRejection()
    {
        var controls = new FakeRiskControlService(new RiskControlSnapshot(
            true,
            false,
            null,
            0m,
            2,
            DateTimeOffset.UtcNow));
        var engine = new RiskEngine(new EmptyExchangeClient(), controls, new RiskSettings());

        var decision = await engine.CheckAsync(CreateSignal());

        Assert.False(decision.IsAllowed);
        Assert.Contains(decision.Reasons, reason => reason.Contains("position reconciliation mismatch", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, controls.RecordRejectionCallCount);
    }

    [Fact]
    public async Task CheckAsync_DailyNetRealizedLossAtLimit_BlocksTradingAndRecordsRejection()
    {
        var controls = new FakeRiskControlService(new RiskControlSnapshot(
            true,
            false,
            null,
            "USDT",
            -490m,
            10m,
            -500m,
            Array.Empty<UnconvertedFee>(),
            0,
            DateTimeOffset.UtcNow));
        var engine = new RiskEngine(new EmptyExchangeClient(), controls, new RiskSettings
        {
            MaxDailyLoss = 500m
        });

        var decision = await engine.CheckAsync(CreateSignal());

        Assert.False(decision.IsAllowed);
        Assert.Contains(decision.Reasons, reason => reason.Contains("Daily net realized loss limit", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, controls.RecordRejectionCallCount);
    }

    [Fact]
    public async Task CheckAsync_UnconvertedFee_BlocksTradingBeforeExchangeLookup()
    {
        var controls = new FakeRiskControlService(new RiskControlSnapshot(
            true,
            false,
            null,
            "USDT",
            -100m,
            2m,
            null,
            [new UnconvertedFee("exec-1", 0.0001m, "BTC", DateTimeOffset.UtcNow)],
            0,
            DateTimeOffset.UtcNow));
        var exchange = new CountingExchangeClient();
        var engine = new RiskEngine(exchange, controls, new RiskSettings());

        var decision = await engine.CheckAsync(CreateSignal());

        Assert.False(decision.IsAllowed);
        Assert.Contains(decision.Reasons, reason => reason.Contains("daily net PnL is incomplete", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(decision.Reasons, reason => reason.Contains("BTC", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, exchange.GetPositionsCallCount);
        Assert.Equal(1, controls.RecordRejectionCallCount);
    }

    [Fact]
    public async Task CheckAsync_EmergencyStop_BlocksTradingWithPersistedReason()
    {
        var controls = new FakeRiskControlService(new RiskControlSnapshot(
            true,
            true,
            "manual incident response",
            0m,
            0,
            DateTimeOffset.UtcNow));
        var engine = new RiskEngine(new EmptyExchangeClient(), controls, new RiskSettings());

        var decision = await engine.CheckAsync(CreateSignal());

        Assert.False(decision.IsAllowed);
        Assert.Contains(decision.Reasons, reason => reason.Contains("manual incident response", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, controls.RecordRejectionCallCount);
    }

    private static TradingSignal CreateSignal() => new()
    {
        Id = Guid.Parse("d0f8625f-2ad4-44eb-a1ec-22acbfbb2e58"),
        Symbol = "BTCUSDT",
        Side = OrderSide.Buy,
        RequestedQuantity = 0.001m,
        CreatedAt = DateTimeOffset.UtcNow,
        Source = "risk-test"
    };

    private sealed class FakeRiskControlService(RiskControlSnapshot snapshot) : IRiskControlService
    {
        public int RecordRejectionCallCount { get; private set; }

        public Task<RiskControlSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);

        public Task<RiskControlSnapshot> SetTradingEnabledAsync(
            bool enabled,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RiskControlSnapshot> SetEmergencyStopAsync(
            bool enabled,
            string? reason,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RecordRejectionAsync(
            TradingSignal signal,
            RiskDecision decision,
            CancellationToken cancellationToken = default)
        {
            RecordRejectionCallCount++;
            return Task.CompletedTask;
        }
    }

    private class EmptyExchangeClient : IExchangeClient
    {
        public virtual Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public virtual Task<IReadOnlyCollection<Position>> GetPositionsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Position>>(Array.Empty<Position>());

        public virtual Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<Order>>(Array.Empty<Order>());

        public virtual Task<OrderResult> PlaceOrderAsync(
            PlaceOrderRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public virtual Task CancelOrderAsync(
            string exchangeOrderId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public virtual Task<Order?> GetOrderAsync(
            string exchangeOrderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Order?>(null);

        public virtual Task<Order?> GetOrderByClientOrderIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Order?>(null);
    }

    private sealed class CountingExchangeClient : EmptyExchangeClient
    {
        public int GetPositionsCallCount { get; private set; }

        public override Task<IReadOnlyCollection<Position>> GetPositionsAsync(
            CancellationToken cancellationToken = default)
        {
            GetPositionsCallCount++;
            return base.GetPositionsAsync(cancellationToken);
        }
    }
}
