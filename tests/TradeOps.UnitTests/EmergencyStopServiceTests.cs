using Microsoft.Extensions.Logging.Abstractions;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Entities;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class EmergencyStopServiceTests
{
    [Fact]
    public async Task SetAsync_Enable_PersistsRiskBeforeBulkCancellationAndReturnsSummary()
    {
        var risk = new FakeRiskControlService();
        var bulk = new FakeBulkCancellationService(new BulkOrderCancellationResult(
            null,
            [
                new OrderCancellationResult(
                    OrderCancellationOutcome.Cancelled,
                    null,
                    "cancelled")
            ]));
        var alerts = new FakeAlertService();
        var service = new EmergencyStopService(
            risk,
            bulk,
            alerts,
            NullLogger<EmergencyStopService>.Instance);

        var result = await service.SetAsync(true, "operator stop");

        Assert.True(result.Risk.EmergencyStop);
        Assert.Equal("operator stop", result.Risk.EmergencyStopReason);
        Assert.NotNull(result.OrderCancellation);
        Assert.Equal(1, bulk.Calls);
        Assert.True(risk.SetCompletedBeforeBulkCall);
        Assert.Empty(alerts.Alerts);
    }

    [Fact]
    public async Task SetAsync_Disable_DoesNotRunBulkCancellation()
    {
        var risk = new FakeRiskControlService();
        var bulk = new FakeBulkCancellationService(new BulkOrderCancellationResult(
            null,
            Array.Empty<OrderCancellationResult>()));
        var alerts = new FakeAlertService();
        var service = new EmergencyStopService(
            risk,
            bulk,
            alerts,
            NullLogger<EmergencyStopService>.Instance);

        var result = await service.SetAsync(false, null);

        Assert.False(result.Risk.EmergencyStop);
        Assert.Null(result.OrderCancellation);
        Assert.Equal(0, bulk.Calls);
        Assert.Empty(alerts.Alerts);
    }

    [Fact]
    public async Task SetAsync_IncompleteCancellation_EmitsCriticalAlert()
    {
        var risk = new FakeRiskControlService();
        var bulk = new FakeBulkCancellationService(new BulkOrderCancellationResult(
            null,
            [
                new OrderCancellationResult(
                    OrderCancellationOutcome.Unresolved,
                    null,
                    "unresolved")
            ]));
        var alerts = new FakeAlertService();
        var service = new EmergencyStopService(
            risk,
            bulk,
            alerts,
            NullLogger<EmergencyStopService>.Instance);

        var result = await service.SetAsync(true, "operator stop");

        Assert.False(result.OrderCancellation!.IsComplete);
        var alert = Assert.Single(alerts.Alerts);
        Assert.Equal("EmergencyStopCancellationIncomplete", alert.EventType);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
    }

    private sealed class FakeRiskControlService : IRiskControlService
    {
        public bool SetCompletedBeforeBulkCall { get; private set; }

        public Task<RiskControlSnapshot> GetSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateSnapshot(false, null));

        public Task<RiskControlSnapshot> SetTradingEnabledAsync(
            bool enabled,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateSnapshot(false, null));

        public Task<RiskControlSnapshot> SetEmergencyStopAsync(
            bool enabled,
            string? reason,
            CancellationToken cancellationToken = default)
        {
            SetCompletedBeforeBulkCall = true;
            return Task.FromResult(CreateSnapshot(enabled, enabled ? reason : null));
        }

        public Task RecordRejectionAsync(
            TradingSignal signal,
            RiskDecision decision,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        private static RiskControlSnapshot CreateSnapshot(bool enabled, string? reason) =>
            new(
                true,
                enabled,
                reason,
                0m,
                0,
                DateTimeOffset.UtcNow);
    }

    private sealed class FakeBulkCancellationService(BulkOrderCancellationResult result)
        : IOrderBulkCancellationService
    {
        public int Calls { get; private set; }

        public Task<BulkOrderCancellationResult> CancelOpenOrdersAsync(
            string? symbol = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
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
}
