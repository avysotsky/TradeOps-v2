using TradeOps.Domain.Enums;

namespace TradeOps.Domain.Entities;

public sealed class TradingSignal
{
    public Guid Id { get; set; }

    public required string Symbol { get; set; }

    public OrderSide Side { get; set; }

    // Kept as a string for Day 1 because the handoff names SignalType
    // but does not define its enum values yet.
    public string? SignalType { get; set; }

    public decimal RequestedQuantity { get; set; }

    public decimal? RiskPercent { get; set; }

    public decimal? StopLoss { get; set; }

    public decimal? TakeProfit { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string? Source { get; set; }

    public SignalOutcome Outcome { get; set; } = SignalOutcome.Received;

    public string[] RiskRejectionReasons { get; set; } = [];

    public Guid? OrderId { get; set; }

    public string? ClientOrderId { get; set; }

    public string? ExecutionIssueCode { get; set; }

    public string? ExecutionIssueMessage { get; set; }

    public DateTimeOffset? ExecutionIssueAt { get; set; }
}
