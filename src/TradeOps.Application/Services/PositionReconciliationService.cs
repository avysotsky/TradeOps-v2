using Microsoft.Extensions.Logging;
using TradeOps.Application.Interfaces;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Services;

public sealed class PositionReconciliationService(
    IPositionService positionService,
    IExchangeClient exchangeClient,
    IRiskEventRepository riskEventRepository,
    IAlertService alertService,
    ILogger<PositionReconciliationService> logger) : IPositionReconciliationService
{
    private const string EventType = "PositionMismatch";

    public async Task<PositionReconciliationSummary> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        var localStates = await positionService.GetLocalAsync(cancellationToken);
        var exchangePositions = await exchangeClient.GetPositionsAsync(cancellationToken);
        var activeEvents = await riskEventRepository.GetActiveByTypeAsync(EventType, cancellationToken);

        var local = BuildLocalExposure(localStates);
        var exchange = BuildExchangeExposure(exchangePositions);
        var symbols = local.Keys
            .Union(exchange.Keys, StringComparer.OrdinalIgnoreCase)
            .Union(activeEvents.Select(riskEvent => riskEvent.EventKey), StringComparer.OrdinalIgnoreCase)
            .OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var issues = new List<PositionReconciliationIssue>();
        var matched = 0;

        foreach (var symbol in symbols)
        {
            local.TryGetValue(symbol, out var localExposure);
            exchange.TryGetValue(symbol, out var exchangeExposure);

            var reason = GetMismatchReason(localExposure, exchangeExposure);
            if (reason is null)
            {
                matched++;
                await ResolveActiveEventAsync(symbol, cancellationToken);
                continue;
            }

            var issue = new PositionReconciliationIssue(
                symbol,
                localExposure?.Side,
                localExposure?.Quantity ?? 0m,
                exchangeExposure?.Side,
                exchangeExposure?.Quantity ?? 0m,
                reason);
            issues.Add(issue);

            await ObserveMismatchAsync(issue, cancellationToken);
        }

        var summary = new PositionReconciliationSummary(
            symbols.Length,
            matched,
            issues.Count,
            issues);

        logger.LogInformation(
            "Position reconciliation completed. Compared={Compared}, Matched={Matched}, Mismatched={Mismatched}.",
            summary.SymbolsCompared,
            summary.Matched,
            summary.Mismatched);

        return summary;
    }

    private async Task ObserveMismatchAsync(
        PositionReconciliationIssue issue,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var active = await riskEventRepository.GetActiveAsync(
            EventType,
            issue.Symbol,
            cancellationToken);

        if (active is not null)
        {
            active.Message = issue.Reason;
            active.LocalSide = issue.LocalSide;
            active.LocalQuantity = issue.LocalQuantity;
            active.ExchangeSide = issue.ExchangeSide;
            active.ExchangeQuantity = issue.ExchangeQuantity;
            active.LastObservedAt = now;
            await riskEventRepository.UpdateAsync(active, cancellationToken);
            return;
        }

        var riskEvent = new RiskEvent
        {
            Id = Guid.NewGuid(),
            EventType = EventType,
            EventKey = issue.Symbol,
            Symbol = issue.Symbol,
            Message = issue.Reason,
            Severity = RiskEventSeverity.Warning,
            LocalSide = issue.LocalSide,
            LocalQuantity = issue.LocalQuantity,
            ExchangeSide = issue.ExchangeSide,
            ExchangeQuantity = issue.ExchangeQuantity,
            CreatedAt = now,
            LastObservedAt = now
        };

        if (!await riskEventRepository.TryAddAsync(riskEvent, cancellationToken))
        {
            return;
        }

        logger.LogWarning(
            "Position mismatch for {Symbol}. Local={LocalSide} {LocalQuantity}; Exchange={ExchangeSide} {ExchangeQuantity}. {Reason}",
            issue.Symbol,
            issue.LocalSide,
            issue.LocalQuantity,
            issue.ExchangeSide,
            issue.ExchangeQuantity,
            issue.Reason);

        await alertService.SendAsync(
            new AlertMessage(
                "PositionMismatch",
                $"{issue.Symbol}: local={Describe(issue.LocalSide, issue.LocalQuantity)}, exchange={Describe(issue.ExchangeSide, issue.ExchangeQuantity)}. {issue.Reason}",
                AlertSeverity.Warning),
            cancellationToken);
    }

    private async Task ResolveActiveEventAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        var active = await riskEventRepository.GetActiveAsync(
            EventType,
            symbol,
            cancellationToken);

        if (active is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        active.ResolvedAt = now;
        active.LastObservedAt = now;
        active.Message = "Local fill-derived exposure matches the exchange exposure.";
        await riskEventRepository.UpdateAsync(active, cancellationToken);

        logger.LogInformation("Position mismatch resolved for {Symbol}.", symbol);

        await alertService.SendAsync(
            new AlertMessage(
                "PositionMismatchResolved",
                $"{symbol}: local and exchange exposure are aligned again.",
                AlertSeverity.Info),
            cancellationToken);
    }

    private static Dictionary<string, Exposure> BuildLocalExposure(
        IReadOnlyCollection<PositionState> states)
    {
        return states
            .Where(state => state.Quantity > 0m && state.Side is not null)
            .ToDictionary(
                state => state.Symbol,
                state => new Exposure(state.Side!.Value, state.Quantity),
                StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, Exposure> BuildExchangeExposure(
        IReadOnlyCollection<Position> positions)
    {
        var result = new Dictionary<string, Exposure>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in positions
            .Where(position => position.Quantity > 0m)
            .GroupBy(position => position.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            var signedQuantity = group.Sum(position =>
                position.Side == OrderSide.Buy
                    ? position.Quantity
                    : -position.Quantity);

            if (signedQuantity == 0m)
            {
                continue;
            }

            result[group.Key] = new Exposure(
                signedQuantity > 0m ? OrderSide.Buy : OrderSide.Sell,
                Math.Abs(signedQuantity));
        }

        return result;
    }

    private static string? GetMismatchReason(Exposure? local, Exposure? exchange)
    {
        if (local is null && exchange is null)
        {
            return null;
        }

        if (local is null)
        {
            return "Exchange has open exposure but local persistent fills reconstruct a flat position.";
        }

        if (exchange is null)
        {
            return "Local persistent fills reconstruct open exposure but the exchange reports a flat position.";
        }

        if (local.Side != exchange.Side)
        {
            return $"Position side mismatch: local={local.Side}, exchange={exchange.Side}.";
        }

        if (local.Quantity != exchange.Quantity)
        {
            return $"Position quantity mismatch: local={local.Quantity}, exchange={exchange.Quantity}.";
        }

        return null;
    }

    private static string Describe(OrderSide? side, decimal quantity) =>
        side is null || quantity == 0m ? "flat" : $"{side} {quantity}";

    private sealed record Exposure(OrderSide Side, decimal Quantity);
}
