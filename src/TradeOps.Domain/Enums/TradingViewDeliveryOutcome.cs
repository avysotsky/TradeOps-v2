namespace TradeOps.Domain.Enums;

public enum TradingViewDeliveryOutcome
{
    Received = 0,
    ValidationRejected = 1,
    Accepted = 2,
    Redelivered = 3,
    RiskRejected = 4,
    Conflict = 5,
    Failed = 6
}
