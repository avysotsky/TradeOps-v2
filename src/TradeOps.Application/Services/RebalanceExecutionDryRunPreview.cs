using TradeOps.Application.Models;
using TradeOps.Domain.Entities;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Services;

public sealed record RebalanceExecutionDryRunResult(
    int SchemaVersion,
    string Mode,
    string State,
    Guid SignalId,
    string DecisionId,
    string? ClientOrderId,
    string Symbol,
    OrderSide Side,
    OrderType OrderType,
    decimal Quantity,
    decimal ReferencePrice,
    decimal EstimatedNotional,
    bool RiskAllowed,
    IReadOnlyCollection<string> RiskReasons,
    bool MutationPerformed,
    bool BrokerRequestSent,
    bool PersistencePerformed);

/// <summary>Offline preview only. No broker execution or state mutation.</summary>
public static class RebalanceExecutionDryRunPreview
{
    public static async Task<RebalanceExecutionDryRunResult> RunAsync(
        RebalancePlan plan,
        RiskSettings settings,
        RiskControlSnapshot controls,
        IReadOnlyCollection<Position> positions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var intent = plan.OrderIntent ??
            throw new ArgumentException("Rebalance order intent is required.", nameof(plan));
        if (plan.Status != RebalancePlanStatus.Ready ||
            string.IsNullOrWhiteSpace(intent.DecisionId) ||
            intent.DecisionId != plan.DecisionId ||
            string.IsNullOrWhiteSpace(intent.Instrument.Symbol) ||
            intent.Quantity <= 0m ||
            intent.ReferencePrice <= 0m ||
            intent.EstimatedNotional <= 0m ||
            !Enum.IsDefined(intent.Side))
            throw new ArgumentException("Invalid rebalance intent.", nameof(plan));

        var signal = RebalancePreviewSignalProjector.Project(plan);
        var risk = await NonMutatingRiskPreview.CheckAsync(
            signal, settings, controls, positions, cancellationToken);
        return new RebalanceExecutionDryRunResult(
            1, "dryRun", risk.IsAllowed ? "Prepared" : "BlockedByRisk",
            signal.Id, intent.DecisionId,
            risk.IsAllowed ? new ClientOrderIdGenerator().Generate(signal.Id) : null,
            intent.Instrument.Symbol, intent.Side, OrderType.Market,
            intent.Quantity, intent.ReferencePrice, intent.EstimatedNotional,
            risk.IsAllowed, risk.Reasons, false, false, false);
    }
}
