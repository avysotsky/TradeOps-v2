using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Services;

public sealed class RiskControlService(
    IOperationalRiskStateRepository operationalStateRepository,
    IFillRepository fillRepository,
    IRiskEventRepository riskEventRepository,
    IAlertService alertService,
    RiskSettings settings,
    AccountingSettings accountingSettings) : IRiskControlService
{
    private const string PositionMismatchEventType = "PositionMismatch";
    private const string RiskRejectedEventType = "RiskRejected";

    public async Task<RiskControlSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await operationalStateRepository.GetOrCreateAsync(
            settings.TradingEnabled,
            settings.EmergencyStop,
            cancellationToken);

        var fills = await fillRepository.GetPositionFillsAsync(
            cancellationToken: cancellationToken);
        var activeMismatches = await riskEventRepository.GetActiveByTypeAsync(
            PositionMismatchEventType,
            cancellationToken);
        var accounting = PnLAccountingCalculator.CalculateDaily(
            fills,
            accountingSettings.SettlementCurrency,
            DateTimeOffset.UtcNow);

        return new RiskControlSnapshot(
            state.TradingEnabled,
            state.EmergencyStop,
            state.EmergencyStopReason,
            accounting.SettlementCurrency,
            accounting.GrossRealizedPnL,
            accounting.SettlementFees,
            accounting.NetRealizedPnL,
            accounting.UnconvertedFees,
            activeMismatches.Count,
            state.UpdatedAt);
    }

    public async Task<RiskControlSnapshot> SetTradingEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var state = await operationalStateRepository.GetOrCreateAsync(
            settings.TradingEnabled,
            settings.EmergencyStop,
            cancellationToken);

        var changed = state.TradingEnabled != enabled;
        state.TradingEnabled = enabled;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await operationalStateRepository.UpdateAsync(state, cancellationToken);

        if (changed)
        {
            await alertService.SendAsync(
                new AlertMessage(
                    enabled ? "TradingEnabled" : "TradingDisabled",
                    enabled
                        ? "TradeOps trading has been enabled."
                        : "TradeOps trading has been disabled.",
                    enabled ? AlertSeverity.Info : AlertSeverity.Warning),
                cancellationToken);
        }

        return await GetSnapshotAsync(cancellationToken);
    }

    public async Task<RiskControlSnapshot> SetEmergencyStopAsync(
        bool enabled,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var state = await operationalStateRepository.GetOrCreateAsync(
            settings.TradingEnabled,
            settings.EmergencyStop,
            cancellationToken);

        var changed = state.EmergencyStop != enabled;
        state.EmergencyStop = enabled;
        state.EmergencyStopReason = enabled
            ? NormalizeReason(reason)
            : null;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        await operationalStateRepository.UpdateAsync(state, cancellationToken);

        if (changed)
        {
            await alertService.SendAsync(
                new AlertMessage(
                    enabled ? "EmergencyStopActivated" : "EmergencyStopCleared",
                    enabled
                        ? $"TradeOps emergency stop activated. {state.EmergencyStopReason}"
                        : "TradeOps emergency stop cleared.",
                    enabled ? AlertSeverity.Critical : AlertSeverity.Info),
                cancellationToken);
        }

        return await GetSnapshotAsync(cancellationToken);
    }

    public async Task RecordRejectionAsync(
        TradingSignal signal,
        RiskDecision decision,
        CancellationToken cancellationToken = default)
    {
        if (decision.IsAllowed)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var riskEvent = new RiskEvent
        {
            Id = Guid.NewGuid(),
            EventType = RiskRejectedEventType,
            EventKey = signal.Id.ToString("N"),
            Symbol = signal.Symbol,
            Message = Truncate(string.Join("; ", decision.Reasons), 1000),
            Severity = RiskEventSeverity.Warning,
            LocalSide = signal.Side,
            LocalQuantity = signal.RequestedQuantity,
            CreatedAt = now,
            LastObservedAt = now,
            ResolvedAt = now
        };

        await riskEventRepository.TryAddAsync(riskEvent, cancellationToken);
    }

    private static string NormalizeReason(string? reason)
    {
        var value = string.IsNullOrWhiteSpace(reason)
            ? "Emergency stop activated."
            : reason.Trim();
        return Truncate(value, 500);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
