using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TradeOps.Application.Models;
using TradeOps.Domain.Entities;

namespace TradeOps.Application.Services;

/// <summary>Canonical deterministic risk-preview signal projection; never persists a signal.</summary>
public static class RebalancePreviewSignalProjector
{
    public static TradingSignal Project(RebalancePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var intent = plan.OrderIntent ?? throw new ArgumentException("A rebalance intent is required.", nameof(plan));
        var stableInput = string.Join("\n",
            intent.DecisionId,
            intent.Instrument.Symbol,
            intent.Side.ToString(),
            intent.Quantity.ToString("G29", CultureInfo.InvariantCulture),
            plan.PlannedAt.ToString("O", CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(stableInput));
        return new TradingSignal
        {
            Id = new Guid(hash.AsSpan(0, 16)),
            Symbol = intent.Instrument.Symbol,
            Side = intent.Side,
            RequestedQuantity = intent.Quantity,
            SignalType = "RebalanceRiskPreview",
            CreatedAt = plan.PlannedAt,
            Source = "transcript-research-rebalance-risk-preview"
        };
    }
}
