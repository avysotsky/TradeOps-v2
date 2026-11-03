using TradeOps.Domain.Enums;

namespace TradeOps.Application.Models;

/// <summary>
/// A LOCAL, synthetic-only order identity used to dry-run future
/// per-provider/per-account idempotency. Not authenticated broker evidence,
/// not an execution command, and not a durable uniqueness reservation.
/// </summary>
public sealed record ScopedOrderFingerprint(
    string ProviderKey,
    string AccountKey,
    Guid LocalOrderId,
    string ClientOrderId,
    string? BrokerOrderId,
    string InstrumentKey,
    OrderSide Side,
    OrderType OrderType,
    decimal RequestedQuantity,
    decimal? Price);

/// <summary>
/// A LOCAL, synthetic-only execution identity. Scoped execution ID is
/// provider + account + external execution ID; order ownership is checked
/// separately so a replay cannot be silently rebound to another order.
/// </summary>
public sealed record ScopedExecutionFingerprint(
    string ProviderKey,
    string AccountKey,
    Guid LocalOrderId,
    string ClientOrderId,
    string BrokerOrderId,
    string ExecutionId,
    decimal Quantity,
    decimal Price,
    decimal? Fee,
    string? FeeCurrency);

public enum ScopedIdempotencyDisposition
{
    /// <summary>
    /// The two records occupy distinct identity keys. Not a permission
    /// to insert, execute or resubmit: a DB-wide check is still needed.
    /// </summary>
    DistinctScopedIdentity = 1,

    /// <summary>
    /// Identical scoped key and complete immutable payload.
    /// </summary>
    EquivalentReplay = 2,

    /// <summary>
    /// Two records collide by local/order/broker/execution identity.
    /// Never choose the most recent response or overwrite either one.
    /// </summary>
    IdentityConflict = 3,

    /// <summary>
    /// New broker ID on an otherwise identical local order. This requires
    /// explicit durable broker-account proof and a separate binding gate.
    /// </summary>
    BrokerBindingRequiresReview = 4
}

public sealed record ScopedIdempotencyAssessment(
    ScopedIdempotencyDisposition Disposition,
    string ReasonCode);

/// <summary>
/// PURE comparisons for future broker/account-scoped persistence. This
/// model intentionally supports only the two SYNTHETIC providers
/// accepted by TO-V2-19, not real IBKR Paper or any live exchange.
/// </summary>
public static class BrokerScopedIdempotencyPolicy
{
    private const int MaxAccountLength = 128;
    private const int MaxOrderKeyLength = 100;
    private const int MaxInstrumentLength = 192;
    private const int MaxFeeCurrencyLength = 20;
    private const decimal MaxMagnitudeExclusive = 10_000_000_000_000_000m;

    public static ScopedIdempotencyAssessment CompareOrders(
        ScopedOrderFingerprint existing,
        ScopedOrderFingerprint incoming)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);
        Validate(existing);
        Validate(incoming);

        var sameLocalOrder = existing.LocalOrderId == incoming.LocalOrderId;
        var sameScope = SameScope(existing.ProviderKey, existing.AccountKey,
            incoming.ProviderKey, incoming.AccountKey);

        // A local GUID is immutable and never reusable across accounts.
        if (!sameScope)
        {
            return sameLocalOrder
                ? Conflict("LocalOrderIdCrossScope")
                : Distinct("DifferentProviderOrAccount");
        }

        var sameClientOrderId = string.Equals(
            existing.ClientOrderId, incoming.ClientOrderId,
            StringComparison.Ordinal);
        var sameBrokerOrderId = existing.BrokerOrderId is not null
            && incoming.BrokerOrderId is not null
            && string.Equals(existing.BrokerOrderId, incoming.BrokerOrderId,
                StringComparison.Ordinal);

        // One OrderId, one cOID; one broker order ID, one cOID.
        if (sameLocalOrder && !sameClientOrderId)
        {
            return Conflict("LocalOrderIdChangedClientIdentity");
        }

        if (!sameClientOrderId && sameBrokerOrderId)
        {
            return Conflict("BrokerOrderIdBoundToAnotherClientId");
        }

        if (!sameClientOrderId)
        {
            return Distinct("DifferentClientAndBrokerIdentifiers");
        }

        if (!sameLocalOrder)
        {
            return Conflict("ClientOrderIdBoundToAnotherLocalOrder");
        }

        var matchingImmutableTerms =
            string.Equals(existing.InstrumentKey, incoming.InstrumentKey,
                StringComparison.Ordinal)
            && existing.Side == incoming.Side
            && existing.OrderType == incoming.OrderType
            && existing.RequestedQuantity == incoming.RequestedQuantity
            && existing.Price == incoming.Price;

        if (!matchingImmutableTerms)
        {
            return Conflict("OrderTermsChanged");
        }

        if (string.Equals(existing.BrokerOrderId, incoming.BrokerOrderId,
            StringComparison.Ordinal))
        {
            return Replay("IdenticalScopedOrderFingerprint");
        }

        if (existing.BrokerOrderId is null
            && incoming.BrokerOrderId is not null)
        {
            return new(
                ScopedIdempotencyDisposition.BrokerBindingRequiresReview,
                "BrokerIdFirstBindingRequiresAuthenticatedProof");
        }

        return Conflict("BrokerIdRemovedOrChanged");
    }

    public static ScopedIdempotencyAssessment CompareExecutions(
        ScopedExecutionFingerprint existing,
        ScopedExecutionFingerprint incoming)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);
        Validate(existing);
        Validate(incoming);

        var sameScope = SameScope(existing.ProviderKey, existing.AccountKey,
            incoming.ProviderKey, incoming.AccountKey);
        var sameLocalOrder = existing.LocalOrderId == incoming.LocalOrderId;

        if (!sameScope)
        {
            return sameLocalOrder
                ? Conflict("ExecutionLocalOrderIdCrossScope")
                : Distinct("DifferentProviderOrAccount");
        }

        if (!string.Equals(existing.ExecutionId, incoming.ExecutionId,
            StringComparison.Ordinal))
        {
            return Distinct("DifferentExecutionId");
        }

        if (!sameLocalOrder
            || !string.Equals(existing.ClientOrderId, incoming.ClientOrderId,
                StringComparison.Ordinal)
            || !string.Equals(existing.BrokerOrderId, incoming.BrokerOrderId,
                StringComparison.Ordinal))
        {
            return Conflict("ExecutionIdReboundToAnotherOrder");
        }

        if (existing.Quantity != incoming.Quantity
            || existing.Price != incoming.Price
            || existing.Fee != incoming.Fee
            || !string.Equals(existing.FeeCurrency, incoming.FeeCurrency,
                StringComparison.Ordinal))
        {
            return Conflict("ExecutionImmutablePayloadChanged");
        }

        return Replay("IdenticalScopedExecutionFingerprint");
    }

    private static void Validate(ScopedOrderFingerprint item)
    {
        ValidateScope(item.ProviderKey, item.AccountKey);
        CheckText(item.ClientOrderId, MaxOrderKeyLength);
        if (item.BrokerOrderId is not null)
        {
            CheckText(item.BrokerOrderId, MaxOrderKeyLength);
        }

        var prefix = item.ProviderKey == "Mock"
            ? "MOCK:" : "SYNTHETIC-IBKR-SYMBOL:";
        CheckText(item.InstrumentKey, MaxInstrumentLength);
        if (!item.InstrumentKey.StartsWith(prefix, StringComparison.Ordinal)
            || item.InstrumentKey.Length <= prefix.Length
            || item.LocalOrderId == Guid.Empty
            || !Enum.IsDefined(item.Side)
            || !Enum.IsDefined(item.OrderType)
            || !ValidAmount(item.RequestedQuantity, false)
            || (item.Price is not null && !ValidAmount(item.Price.Value, false)))
        {
            throw Invalid();
        }
    }

    private static void Validate(ScopedExecutionFingerprint item)
    {
        ValidateScope(item.ProviderKey, item.AccountKey);
        CheckText(item.ClientOrderId, MaxOrderKeyLength);
        CheckText(item.BrokerOrderId, MaxOrderKeyLength);
        CheckText(item.ExecutionId, MaxOrderKeyLength);

        if (item.LocalOrderId == Guid.Empty
            || !ValidAmount(item.Quantity, false)
            || !ValidAmount(item.Price, false)
            || (item.Fee is not null && !ValidAmount(item.Fee.Value, true)))
        {
            throw Invalid();
        }

        if (item.FeeCurrency is not null)
        {
            CheckText(item.FeeCurrency, MaxFeeCurrencyLength);
        }
    }

    private static void ValidateScope(string provider, string account)
    {
        if (provider is not ("Mock" or "IbkrOffline"))
        {
            throw Invalid();
        }

        CheckText(account, MaxAccountLength);
    }

    private static void CheckText(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value != value.Trim()
            || value.Any(char.IsControl))
        {
            throw Invalid();
        }
    }

    private static bool ValidAmount(decimal value, bool allowZero) =>
        (allowZero ? value >= 0m : value > 0m)
        && value < MaxMagnitudeExclusive
        && decimal.Round(value, 12) == value;

    private static bool SameScope(
        string firstProvider, string firstAccount,
        string secondProvider, string secondAccount) =>
        string.Equals(firstProvider, secondProvider, StringComparison.Ordinal)
        && string.Equals(firstAccount, secondAccount, StringComparison.Ordinal);

    private static ScopedIdempotencyAssessment Distinct(string reason) =>
        new(ScopedIdempotencyDisposition.DistinctScopedIdentity, reason);

    private static ScopedIdempotencyAssessment Replay(string reason) =>
        new(ScopedIdempotencyDisposition.EquivalentReplay, reason);

    private static ScopedIdempotencyAssessment Conflict(string reason) =>
        new(ScopedIdempotencyDisposition.IdentityConflict, reason);

    private static InvalidOperationException Invalid() =>
        new("Invalid synthetic scoped idempotency fingerprint; no database or broker action permitted.");
}
