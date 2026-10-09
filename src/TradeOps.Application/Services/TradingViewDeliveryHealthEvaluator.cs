using System.Globalization;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Services;

public sealed class TradingViewDeliveryHealthEvaluator(
    ITradingViewDeliveryAuditRepository deliveryAuditRepository,
    ITradingViewDeliveryHealthStateRepository healthStateRepository,
    IAlertService alertService,
    TradingViewDeliveryHealthSettings settings)
    : ITradingViewDeliveryHealthEvaluator
{
    public async Task<TradingViewDeliveryHealthEvaluation> EvaluateAsync(
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var from = now.AddMinutes(-settings.LookbackMinutes);

        var metrics = await deliveryAuditRepository.GetMetricsAsync(
            from,
            now,
            cancellationToken);

        var evaluation = Evaluate(metrics, now, settings);
        var previous = await healthStateRepository.GetAsync(
            cancellationToken);

        var state = new TradingViewDeliveryHealthState
        {
            Id = TradingViewDeliveryHealthState.SingletonId,
            Status = evaluation.Status,
            Reason = evaluation.Reason,
            UpdatedAt = now,
            WindowFrom = metrics.FromInclusive,
            WindowTo = metrics.ToExclusive,
            Total = metrics.Total,
            Failed = metrics.Failed,
            Conflict = metrics.Conflict,
            RiskRejected = metrics.RiskRejected,
            AverageLatencyMilliseconds =
                metrics.AverageLatencyMilliseconds,
            LatestDeliveryAt = metrics.LatestDeliveryAt,
            LatestSuccessfulAt = metrics.LatestSuccessfulAt
        };

        await healthStateRepository.UpsertAsync(
            state,
            cancellationToken);

        if (previous?.Status != evaluation.Status)
        {
            await SendTransitionAlertAsync(
                previous?.Status,
                evaluation,
                cancellationToken);
        }

        return evaluation;
    }

    public static TradingViewDeliveryHealthEvaluation Evaluate(
        TradingViewDeliveryMetricsSnapshot metrics,
        DateTimeOffset now,
        TradingViewDeliveryHealthSettings settings)
    {
        var criticalReasons = new List<string>();

        var noSuccessOverdue =
            settings.NoSuccessfulDeliveryMinutes > 0
            && metrics.LatestDeliveryAt.HasValue
            && (!metrics.LatestSuccessfulAt.HasValue
                || now - metrics.LatestSuccessfulAt.Value
                    >= TimeSpan.FromMinutes(
                        settings.NoSuccessfulDeliveryMinutes));

        if (noSuccessOverdue)
        {
            criticalReasons.Add(
                metrics.LatestSuccessfulAt.HasValue
                    ? $"No successful TradingView delivery for at least {settings.NoSuccessfulDeliveryMinutes} minutes."
                    : "TradingView deliveries exist but none has completed successfully.");
        }

        var enoughData =
            metrics.Total >= settings.MinimumDeliveries;

        if (enoughData)
        {
            var failureRate = Percentage(
                metrics.Failed,
                metrics.Total);

            if (metrics.Failed >= settings.CriticalFailureCount)
            {
                criticalReasons.Add(
                    $"Failed deliveries {metrics.Failed} reached the critical count threshold {settings.CriticalFailureCount}.");
            }

            if (failureRate
                >= settings.CriticalFailureRatePercent)
            {
                criticalReasons.Add(
                    $"Failed delivery rate {FormatPercent(failureRate)} reached the critical threshold {FormatPercent(settings.CriticalFailureRatePercent)}.");
            }
        }

        if (criticalReasons.Count > 0)
        {
            return new TradingViewDeliveryHealthEvaluation(
                TradingViewDeliveryHealthStatus.Critical,
                string.Join(" ", criticalReasons),
                metrics);
        }

        var degradedReasons = new List<string>();

        if (enoughData)
        {
            var conflictRate = Percentage(
                metrics.Conflict,
                metrics.Total);
            var riskRejectedRate = Percentage(
                metrics.RiskRejected,
                metrics.Total);

            if (conflictRate
                >= settings.DegradedConflictRatePercent)
            {
                degradedReasons.Add(
                    $"Conflict rate {FormatPercent(conflictRate)} reached the degraded threshold {FormatPercent(settings.DegradedConflictRatePercent)}.");
            }

            if (riskRejectedRate
                >= settings.DegradedRiskRejectedRatePercent)
            {
                degradedReasons.Add(
                    $"Risk-rejected delivery rate {FormatPercent(riskRejectedRate)} reached the degraded threshold {FormatPercent(settings.DegradedRiskRejectedRatePercent)}.");
            }

            if (metrics.AverageLatencyMilliseconds.HasValue
                && metrics.AverageLatencyMilliseconds.Value
                    >= settings.MaxAverageLatencyMilliseconds)
            {
                degradedReasons.Add(
                    $"Average adapter latency {metrics.AverageLatencyMilliseconds.Value:F0} ms reached the degraded threshold {settings.MaxAverageLatencyMilliseconds} ms.");
            }
        }

        if (degradedReasons.Count > 0)
        {
            return new TradingViewDeliveryHealthEvaluation(
                TradingViewDeliveryHealthStatus.Degraded,
                string.Join(" ", degradedReasons),
                metrics);
        }

        if (!enoughData)
        {
            return new TradingViewDeliveryHealthEvaluation(
                TradingViewDeliveryHealthStatus.InsufficientData,
                $"Only {metrics.Total} deliveries are present in the lookback window; {settings.MinimumDeliveries} are required for rate-based health evaluation.",
                metrics);
        }

        return new TradingViewDeliveryHealthEvaluation(
            TradingViewDeliveryHealthStatus.Healthy,
            "TradingView delivery health is within configured thresholds.",
            metrics);
    }

    private async Task SendTransitionAlertAsync(
        TradingViewDeliveryHealthStatus? previousStatus,
        TradingViewDeliveryHealthEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        if (evaluation.Status
            is TradingViewDeliveryHealthStatus.Degraded
            or TradingViewDeliveryHealthStatus.Critical)
        {
            var severity = evaluation.Status
                == TradingViewDeliveryHealthStatus.Critical
                    ? AlertSeverity.Critical
                    : AlertSeverity.Warning;

            await alertService.SendAsync(
                new AlertMessage(
                    "TradingViewDeliveryHealthChanged",
                    $"TradingView delivery health changed from {previousStatus?.ToString() ?? "Unknown"} to {evaluation.Status}. {evaluation.Reason}",
                    severity,
                    TradingViewDeliveryHealthState.SingletonId),
                cancellationToken);

            return;
        }

        if ((previousStatus
                is TradingViewDeliveryHealthStatus.Degraded
                or TradingViewDeliveryHealthStatus.Critical)
            && evaluation.Status
                == TradingViewDeliveryHealthStatus.Healthy)
        {
            await alertService.SendAsync(
                new AlertMessage(
                    "TradingViewDeliveryHealthRecovered",
                    $"TradingView delivery health changed from {previousStatus} to {evaluation.Status}. {evaluation.Reason}",
                    AlertSeverity.Info,
                    TradingViewDeliveryHealthState.SingletonId),
                cancellationToken);
        }
    }

    private static decimal Percentage(
        int value,
        int total)
    {
        return total <= 0
            ? 0m
            : (decimal)value * 100m / total;
    }

    private static string FormatPercent(decimal value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture) + "%";
}
