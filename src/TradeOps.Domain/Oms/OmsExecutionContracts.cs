using System;

namespace TradeOps.Domain.Oms;

/// <summary>
/// The identity is unique inside an explicit adapter-defined namespace and account.
/// Some FIX venues reset ExecID at a session boundary; the namespace must encode
/// that documented venue-specific behavior. Never deduplicate on raw ID globally.
/// </summary>
public sealed record OmsExecutionIdentity
{
    public ScopedExecutionId ScopedId { get; }
    public OmsLocalOrderId LocalOrderId { get; }

    public OmsExecutionIdentity(ScopedExecutionId scopedId, OmsLocalOrderId localOrderId)
    {
        ArgumentNullException.ThrowIfNull(scopedId.Scope);
        OmsGuard.Opaque(scopedId.Namespace.Value, nameof(scopedId));
        OmsGuard.Opaque(scopedId.ExecutionId.Value, nameof(scopedId));
        OmsGuard.NonEmpty(localOrderId.Value, nameof(localOrderId));
        ScopedId = scopedId;
        LocalOrderId = localOrderId;
    }
}

public sealed record OmsExecution
{
    public OmsExecutionIdentity Identity { get; }
    public decimal Quantity { get; }
    public OmsQuantityUnit Unit { get; }
    public decimal Price { get; }
    public DateTimeOffset ExecutedAt { get; }

    public OmsExecution(OmsExecutionIdentity identity, decimal quantity,
        OmsQuantityUnit unit, decimal price, DateTimeOffset executedAt)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        if (quantity <= 0m) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (price <= 0m) throw new ArgumentOutOfRangeException(nameof(price));
        if (!Enum.IsDefined(unit)) throw new ArgumentOutOfRangeException(nameof(unit));
        Unit = unit;
        Quantity = quantity;
        Price = price;
        ExecutedAt = executedAt;
    }
}

public enum OmsExecutionDeduplication
{
    New = 1,
    Duplicate = 2,
    Collision = 3
}

/// <summary>Pure idempotency comparison; B2 must implement this with an atomic DB uniqueness guard.</summary>
public static class OmsExecutionContract
{
    public static OmsExecutionDeduplication Compare(OmsExecution? persisted, OmsExecution incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (persisted is null || persisted.Identity.ScopedId != incoming.Identity.ScopedId)
            return OmsExecutionDeduplication.New;
        return persisted == incoming
            ? OmsExecutionDeduplication.Duplicate
            : OmsExecutionDeduplication.Collision;
    }
}
