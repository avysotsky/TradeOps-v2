namespace TradeOps.Domain.Enums;

public enum TradingViewDeliveryHealthStatus
{
    Unknown = 0,
    InsufficientData = 1,
    Healthy = 2,
    Degraded = 3,
    Critical = 4
}
