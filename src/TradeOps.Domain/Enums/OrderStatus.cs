namespace TradeOps.Domain.Enums;

public enum OrderStatus
{
    Created = 1,
    Submitted = 2,
    Accepted = 3,
    PartiallyFilled = 4,
    Filled = 5,
    Cancelled = 6,
    Rejected = 7,
    Unknown = 8
}
