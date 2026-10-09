using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Services;

public sealed class RiskEngine(
    IExchangeClient exchangeClient,
    IRiskControlService riskControlService,
    RiskSettings settings) : IRiskEngine
{
    public async Task<RiskDecision> CheckAsync(
        TradingSignal signal,
        CancellationToken cancellationToken = default)
    {
        var reasons = new List<string>();
        var control = await riskControlService.GetSnapshotAsync(cancellationToken);

        if (!control.TradingEnabled)
        {
            reasons.Add("Trading is disabled by persistent operational state.");
        }

        if (control.EmergencyStop)
        {
            reasons.Add(string.IsNullOrWhiteSpace(control.EmergencyStopReason)
                ? "Emergency stop is active."
                : $"Emergency stop is active: {control.EmergencyStopReason}");
        }

        if (control.HasPositionMismatch)
        {
            reasons.Add(
                $"Trading is blocked because {control.ActivePositionMismatchCount} active position reconciliation mismatch(es) exist.");
        }

        if (!settings.AllowedSymbols.Contains(signal.Symbol))
        {
            reasons.Add($"Symbol '{signal.Symbol}' is not allowed.");
        }

        if (signal.RequestedQuantity <= 0)
        {
            reasons.Add("Requested quantity must be greater than zero.");
        }
        else if (signal.RequestedQuantity > settings.MaxOrderSize)
        {
            reasons.Add(
                $"Order size {signal.RequestedQuantity} exceeds max order size {settings.MaxOrderSize}.");
        }

        if (!control.IsDailyAccountingComplete)
        {
            var currencies = control.UnconvertedFees
                .Select(fee => fee.Currency ?? "UNKNOWN")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(currency => currency, StringComparer.OrdinalIgnoreCase);

            reasons.Add(
                $"Trading is blocked because daily net PnL is incomplete. Unconverted fee currencies: {string.Join(", ", currencies)}.");
        }
        else if (control.DailyNetRealizedPnL <= -settings.MaxDailyLoss)
        {
            reasons.Add(
                $"Daily net realized loss limit reached. Current daily net realized PnL: {control.DailyNetRealizedPnL}.");
        }

        if (reasons.Count > 0)
        {
            return await RejectAsync(signal, reasons, cancellationToken);
        }

        var positions = await exchangeClient.GetPositionsAsync(cancellationToken);
        var currentPosition = positions.FirstOrDefault(
            position => string.Equals(
                position.Symbol,
                signal.Symbol,
                StringComparison.OrdinalIgnoreCase));

        var isNewPosition = currentPosition is null || currentPosition.Quantity == 0;

        if (isNewPosition && positions.Count >= settings.MaxOpenPositions)
        {
            reasons.Add(
                $"Max open positions limit reached: {settings.MaxOpenPositions}.");
        }

        var currentSignedQuantity = currentPosition is null
            ? 0m
            : currentPosition.Side == OrderSide.Buy
                ? currentPosition.Quantity
                : -currentPosition.Quantity;

        var requestedSignedQuantity = signal.Side == OrderSide.Buy
            ? signal.RequestedQuantity
            : -signal.RequestedQuantity;

        var projectedAbsolutePosition = Math.Abs(
            currentSignedQuantity + requestedSignedQuantity);

        if (projectedAbsolutePosition > settings.MaxPositionSize)
        {
            reasons.Add(
                $"Projected position {projectedAbsolutePosition} exceeds max position size {settings.MaxPositionSize}.");
        }

        return reasons.Count == 0
            ? RiskDecision.Allowed()
            : await RejectAsync(signal, reasons, cancellationToken);
    }

    private async Task<RiskDecision> RejectAsync(
        TradingSignal signal,
        IReadOnlyCollection<string> reasons,
        CancellationToken cancellationToken)
    {
        var decision = new RiskDecision(false, reasons);
        await riskControlService.RecordRejectionAsync(signal, decision, cancellationToken);
        return decision;
    }
}
