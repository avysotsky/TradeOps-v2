using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;

namespace TradeOps.Application.Services;

/// <summary>
/// Runs the production RiskEngine against explicitly supplied read-only snapshots.
/// No broker, database, rejection-recording, or operational-state mutation is possible.
/// </summary>
public static class NonMutatingRiskPreview
{
    public static Task<RiskDecision> CheckAsync(
        TradingSignal signal,
        RiskSettings settings,
        RiskControlSnapshot controls,
        IReadOnlyCollection<Position> positions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(positions);
        var engine = new RiskEngine(
            new SnapshotExchangeClient(positions),
            new SnapshotRiskControlService(controls),
            settings);
        return engine.CheckAsync(signal, cancellationToken);
    }

    private sealed class SnapshotRiskControlService(RiskControlSnapshot snapshot) : IRiskControlService
    {
        public Task<RiskControlSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public Task<RiskControlSnapshot> SetTradingEnabledAsync(
            bool enabled, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Preview cannot modify operational risk state.");

        public Task<RiskControlSnapshot> SetEmergencyStopAsync(
            bool enabled, string? reason, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Preview cannot modify emergency stop.");

        public Task RecordRejectionAsync(
            TradingSignal signal, RiskDecision decision, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class SnapshotExchangeClient(IReadOnlyCollection<Position> positions) : IExchangeClient
    {
        public Task<IReadOnlyCollection<Position>> GetPositionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(positions);

        public Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Account requests are prohibited in preview.");

        public Task<IReadOnlyCollection<Order>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Order reads are prohibited in preview.");

        public Task<OrderResult> PlaceOrderAsync(
            PlaceOrderRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Order placement is prohibited in preview.");

        public Task CancelOrderAsync(string exchangeOrderId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Order cancellation is prohibited in preview.");

        public Task<Order?> GetOrderAsync(string exchangeOrderId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Order reads are prohibited in preview.");

        public Task<Order?> GetOrderByClientOrderIdAsync(
            string clientOrderId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Order reads are prohibited in preview.");
    }
}
