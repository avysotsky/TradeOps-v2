using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Application.Services;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class TradingViewDeliveryHealthEvaluatorTests
{
    [Fact]
    public async Task CriticalState_IsPersisted_AndAlertedOnlyOnceWhileUnchanged()
    {
        var now = DateTimeOffset.UtcNow;
        var metrics = Snapshot(
            now,
            total: 10,
            accepted: 7,
            failed: 3,
            latestDeliveryAt: now.AddSeconds(-5),
            latestSuccessfulAt: now.AddSeconds(-10));

        var auditRepository =
            new StubDeliveryAuditRepository(metrics);
        var stateRepository =
            new InMemoryHealthStateRepository();
        var alertService = new RecordingAlertService();
        var settings = Settings();

        var evaluator = new TradingViewDeliveryHealthEvaluator(
            auditRepository,
            stateRepository,
            alertService,
            settings);

        var first = await evaluator.EvaluateAsync();
        var second = await evaluator.EvaluateAsync();

        Assert.Equal(
            TradingViewDeliveryHealthStatus.Critical,
            first.Status);
        Assert.Equal(
            TradingViewDeliveryHealthStatus.Critical,
            second.Status);

        Assert.NotNull(stateRepository.Current);
        Assert.Equal(
            TradingViewDeliveryHealthStatus.Critical,
            stateRepository.Current.Status);
        Assert.Equal(3, stateRepository.Current.Failed);

        var alert = Assert.Single(alertService.Messages);
        Assert.Equal(
            "TradingViewDeliveryHealthChanged",
            alert.EventType);
        Assert.Equal(
            AlertSeverity.Critical,
            alert.Severity);
    }

    [Fact]
    public async Task HealthyAfterCritical_SendsSingleRecoveryAlert()
    {
        var now = DateTimeOffset.UtcNow;
        var metrics = Snapshot(
            now,
            total: 10,
            accepted: 10,
            failed: 0,
            latestDeliveryAt: now.AddSeconds(-3),
            latestSuccessfulAt: now.AddSeconds(-3));

        var auditRepository =
            new StubDeliveryAuditRepository(metrics);
        var stateRepository =
            new InMemoryHealthStateRepository
            {
                Current = new TradingViewDeliveryHealthState
                {
                    Status =
                        TradingViewDeliveryHealthStatus.Critical,
                    Reason = "Previous incident.",
                    UpdatedAt = now.AddMinutes(-1),
                    WindowFrom = now.AddMinutes(-16),
                    WindowTo = now.AddMinutes(-1)
                }
            };
        var alertService = new RecordingAlertService();

        var evaluator = new TradingViewDeliveryHealthEvaluator(
            auditRepository,
            stateRepository,
            alertService,
            Settings());

        var result = await evaluator.EvaluateAsync();

        Assert.Equal(
            TradingViewDeliveryHealthStatus.Healthy,
            result.Status);

        var alert = Assert.Single(alertService.Messages);
        Assert.Equal(
            "TradingViewDeliveryHealthRecovered",
            alert.EventType);
        Assert.Equal(AlertSeverity.Info, alert.Severity);
    }

    [Fact]
    public void SingleConflict_BelowMinimumSample_IsInsufficientData()
    {
        var now = DateTimeOffset.UtcNow;
        var metrics = Snapshot(
            now,
            total: 1,
            accepted: 0,
            conflict: 1,
            latestDeliveryAt: now.AddSeconds(-1));

        var result = TradingViewDeliveryHealthEvaluator.Evaluate(
            metrics,
            now,
            Settings());

        Assert.Equal(
            TradingViewDeliveryHealthStatus.InsufficientData,
            result.Status);
    }

    [Fact]
    public void NoSuccessfulDeliveryCheck_IsOptIn()
    {
        var now = DateTimeOffset.UtcNow;
        var metrics = Snapshot(
            now,
            total: 1,
            accepted: 0,
            latestDeliveryAt: now.AddMinutes(-30),
            latestSuccessfulAt: null);

        var disabled = Settings();
        disabled.NoSuccessfulDeliveryMinutes = 0;

        var disabledResult =
            TradingViewDeliveryHealthEvaluator.Evaluate(
                metrics,
                now,
                disabled);

        Assert.Equal(
            TradingViewDeliveryHealthStatus.InsufficientData,
            disabledResult.Status);

        var enabled = Settings();
        enabled.NoSuccessfulDeliveryMinutes = 15;

        var enabledResult =
            TradingViewDeliveryHealthEvaluator.Evaluate(
                metrics,
                now,
                enabled);

        Assert.Equal(
            TradingViewDeliveryHealthStatus.Critical,
            enabledResult.Status);
    }

    private static TradingViewDeliveryHealthSettings Settings() =>
        new()
        {
            Enabled = true,
            EvaluationIntervalSeconds = 60,
            LookbackMinutes = 15,
            MinimumDeliveries = 5,
            CriticalFailureCount = 2,
            CriticalFailureRatePercent = 20m,
            DegradedConflictRatePercent = 20m,
            DegradedRiskRejectedRatePercent = 50m,
            MaxAverageLatencyMilliseconds = 1000,
            NoSuccessfulDeliveryMinutes = 0
        };

    private static TradingViewDeliveryMetricsSnapshot Snapshot(
        DateTimeOffset now,
        int total,
        int accepted,
        int failed = 0,
        int conflict = 0,
        int riskRejected = 0,
        double? averageLatencyMilliseconds = 50,
        DateTimeOffset? latestDeliveryAt = null,
        DateTimeOffset? latestSuccessfulAt = null)
    {
        return new TradingViewDeliveryMetricsSnapshot(
            now,
            now.AddMinutes(-15),
            now,
            total,
            Pending: 0,
            Accepted: accepted,
            Redelivered: 0,
            ValidationRejected: 0,
            RiskRejected: riskRejected,
            Conflict: conflict,
            Failed: failed,
            AverageLatencyMilliseconds:
                averageLatencyMilliseconds,
            MaxLatencyMilliseconds:
                averageLatencyMilliseconds.HasValue
                    ? (long)averageLatencyMilliseconds.Value
                    : null,
            LatestDeliveryAt: latestDeliveryAt,
            LatestSuccessfulAt: latestSuccessfulAt);
    }

    private sealed class StubDeliveryAuditRepository(
        TradingViewDeliveryMetricsSnapshot snapshot)
        : ITradingViewDeliveryAuditRepository
    {
        public Task<TradingViewDeliveryMetricsSnapshot> GetMetricsAsync(
            DateTimeOffset fromInclusive,
            DateTimeOffset toExclusive,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot with
            {
                FromInclusive = fromInclusive,
                ToExclusive = toExclusive
            });

        public Task<Guid> StartAsync(
            string? eventId,
            string? symbol,
            string? action,
            DateTimeOffset receivedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CompleteAsync(
            Guid deliveryId,
            TradingViewDeliveryOutcome outcome,
            int httpStatusCode,
            Guid? signalId = null,
            Guid? orderId = null,
            string? clientOrderId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyCollection<TradingViewDeliveryAudit>>
            GetAsync(
                string? eventId,
                int limit,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class InMemoryHealthStateRepository
        : ITradingViewDeliveryHealthStateRepository
    {
        public TradingViewDeliveryHealthState? Current { get; set; }

        public Task<TradingViewDeliveryHealthState?> GetAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);

        public Task UpsertAsync(
            TradingViewDeliveryHealthState state,
            CancellationToken cancellationToken = default)
        {
            Current = state;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAlertService : IAlertService
    {
        public List<AlertMessage> Messages { get; } = [];

        public Task SendAsync(
            AlertMessage alert,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(alert);
            return Task.CompletedTask;
        }
    }
}
