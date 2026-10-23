namespace TradeOps.Infrastructure.Persistence;

/// <summary>
/// Standalone synthetic-only reservation. It is NOT an Order entity,
/// command, approval, or broker submission and is deliberately NOT
/// connected to the globally unique legacy Orders.ClientOrderId.
/// </summary>
public sealed class SyntheticScopedOrderReservation
{
    public required string ProviderKey { get; set; }
    public required string AccountKey { get; set; }
    public required string ClientOrderId { get; set; }
    public Guid LocalOrderId { get; set; }
    // A reservation is permitted only AFTER a synthetic broker ID exists.
    // First binding of an unbound ID is NOT authorized by this ledger.
    public required string BrokerOrderId { get; set; }
    public required string InstrumentKey { get; set; }
    public required string Side { get; set; }
    public required string OrderType { get; set; }
    public decimal RequestedQuantity { get; set; }
    public decimal? Price { get; set; }
    public string Classification { get; set; } = "SyntheticOnly";
    public DateTimeOffset ReservedAt { get; set; }
}

/// <summary>
/// One execution reservation per provider + account + execution ID.
/// Composite FK binds it to the precise parent local order, client ID and
/// broker order ID; an execution cannot be rebound to a different order.
/// </summary>
public sealed class SyntheticScopedExecutionReservation
{
    public required string ProviderKey { get; set; }
    public required string AccountKey { get; set; }
    public required string ExecutionId { get; set; }
    public required string ClientOrderId { get; set; }
    public Guid LocalOrderId { get; set; }
    public required string BrokerOrderId { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal? Fee { get; set; }
    public string? FeeCurrency { get; set; }
    public string Classification { get; set; } = "SyntheticOnly";
    public DateTimeOffset ReservedAt { get; set; }
}
