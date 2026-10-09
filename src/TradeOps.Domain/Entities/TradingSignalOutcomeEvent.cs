using TradeOps.Domain.Enums;

namespace TradeOps.Domain.Entities;

public sealed class TradingSignalOutcomeEvent
{
    public Guid Id { get; set; }

    public Guid TradingSignalId { get; set; }

    public SignalOutcome? PreviousOutcome { get; set; }

    public SignalOutcome Outcome { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string[] RiskRejectionReasons { get; set; } = [];

    public Guid? OrderId { get; set; }

    public string? ClientOrderId { get; set; }
}
