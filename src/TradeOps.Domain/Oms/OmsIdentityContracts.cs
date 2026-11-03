using System;
using TradeOps.Domain.Enums;

namespace TradeOps.Domain.Oms;

/// <summary>Wire/persistence schema for additive typed OMS contracts; bump on breaking changes.</summary>
public static class OmsContractSchema
{
    public const int Version = 1;
}

/// <summary>Value objects are intentionally independent of the legacy EF Order/Fill entities.</summary>
internal static class OmsGuard
{
    public static string Opaque(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
            throw new ArgumentException("Identifier must be non-empty and have no surrounding whitespace.", parameter);
        return value;
    }

    public static Guid NonEmpty(Guid value, string parameter) =>
        value == Guid.Empty ? throw new ArgumentException("Identifier GUID must not be empty.", parameter) : value;
}

public enum OmsEnvironment
{
    Simulated = 1,
    Paper = 2,
    Testnet = 3,
    Mainnet = 4
}

public readonly record struct TenantId
{
    public string Value { get; }
    public TenantId(string value) => Value = OmsGuard.Opaque(value, nameof(value));
    public override string ToString() => Value;
}

public readonly record struct VenueId
{
    public string Value { get; }
    // Venue names are registry keys, not opaque exchange identifiers.
    public VenueId(string value) => Value = OmsGuard.Opaque(value, nameof(value)).ToUpperInvariant();
    public override string ToString() => Value;
}

public readonly record struct TradingAccountId
{
    public string Value { get; }
    public TradingAccountId(string value) => Value = OmsGuard.Opaque(value, nameof(value));
    public override string ToString() => Value;
}

public readonly record struct TradingSubaccountId
{
    public string Value { get; }
    public TradingSubaccountId(string value) => Value = OmsGuard.Opaque(value, nameof(value));
    public override string ToString() => Value;
}

/// <summary>Mandatory isolation boundary for every OMS correlation and uniqueness check.</summary>
public sealed record AccountKey
{
    public TenantId Tenant { get; }
    public VenueId Venue { get; }
    public OmsEnvironment Environment { get; }
    public TradingAccountId Account { get; }
    public TradingSubaccountId? Subaccount { get; }

    public AccountKey(TenantId tenant, VenueId venue, OmsEnvironment environment,
        TradingAccountId account, TradingSubaccountId? subaccount = null)
    {
        OmsGuard.Opaque(tenant.Value, nameof(tenant));
        OmsGuard.Opaque(venue.Value, nameof(venue));
        OmsGuard.Opaque(account.Value, nameof(account));
        if (!Enum.IsDefined(environment))
            throw new ArgumentOutOfRangeException(nameof(environment));
        if (subaccount.HasValue)
            OmsGuard.Opaque(subaccount.Value.Value, nameof(subaccount));

        Tenant = tenant;
        Venue = venue;
        Environment = environment;
        Account = account;
        Subaccount = subaccount;
    }
}

public readonly record struct OmsLocalOrderId
{
    public Guid Value { get; }
    public OmsLocalOrderId(Guid value) => Value = OmsGuard.NonEmpty(value, nameof(value));
}

public readonly record struct OrderIntentId
{
    public Guid Value { get; }
    public OrderIntentId(Guid value) => Value = OmsGuard.NonEmpty(value, nameof(value));
}

public readonly record struct OmsCommandId
{
    public Guid Value { get; }
    public OmsCommandId(Guid value) => Value = OmsGuard.NonEmpty(value, nameof(value));
}

public readonly record struct ClientOrderLabel
{
    public string Value { get; }
    public ClientOrderLabel(string value) => Value = OmsGuard.Opaque(value, nameof(value));
    public override string ToString() => Value;
}

public readonly record struct ExchangeOrderKey
{
    public string Value { get; }
    public ExchangeOrderKey(string value) => Value = OmsGuard.Opaque(value, nameof(value));
    public override string ToString() => Value;
}

public readonly record struct ExecutionKey
{
    public string Value { get; }
    public ExecutionKey(string value) => Value = OmsGuard.Opaque(value, nameof(value));
    public override string ToString() => Value;
}

/// <summary>Adapter-defined execution ID namespace, e.g. exchange trade IDs vs FIX session ExecID.</summary>
public readonly record struct ExecutionNamespace
{
    public string Value { get; }
    public ExecutionNamespace(string value) => Value = OmsGuard.Opaque(value, nameof(value));
}

public readonly record struct FixClOrdId
{
    public string Value { get; }
    public FixClOrdId(string value) => Value = OmsGuard.Opaque(value, nameof(value));
}

public readonly record struct FixOrigClOrdId
{
    public string Value { get; }
    public FixOrigClOrdId(string value) => Value = OmsGuard.Opaque(value, nameof(value));
}

/// <summary>FIX tag 11 and tag 41 are different message roles; neither is an exchange OrderID.</summary>
public sealed record FixOrderCorrelation(FixClOrdId ClOrdId, FixOrigClOrdId? OrigClOrdId)
{
    public FixOrderCorrelation Validate()
    {
        OmsGuard.Opaque(ClOrdId.Value, nameof(ClOrdId));
        if (OrigClOrdId.HasValue)
            OmsGuard.Opaque(OrigClOrdId.Value.Value, nameof(OrigClOrdId));
        return this;
    }
}

public readonly record struct OrderVersion
{
    public long Value { get; }
    public OrderVersion(long value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }
}

public readonly record struct OrderRevision
{
    public int Value { get; }
    public OrderRevision(int value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }
}

public readonly record struct DispatchAttempt
{
    public int Value { get; }
    public DispatchAttempt(int value)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }
}

/// <summary>Globally local GUID remains additionally scoped; exchange-only orders have no local intent.</summary>
public sealed record OmsOrderIdentity
{
    public AccountKey Scope { get; }
    public OmsLocalOrderId LocalId { get; }
    public OrderIntentId? IntentId { get; }
    public ClientOrderLabel? ClientLabel { get; }
    public ExchangeOrderKey? ExchangeId { get; }

    public OmsOrderIdentity(AccountKey scope, OmsLocalOrderId localId,
        OrderIntentId? intentId, ClientOrderLabel? clientLabel, ExchangeOrderKey? exchangeId)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        OmsGuard.NonEmpty(localId.Value, nameof(localId));
        if (intentId.HasValue) OmsGuard.NonEmpty(intentId.Value.Value, nameof(intentId));
        if (clientLabel.HasValue) OmsGuard.Opaque(clientLabel.Value.Value, nameof(clientLabel));
        if (exchangeId.HasValue) OmsGuard.Opaque(exchangeId.Value.Value, nameof(exchangeId));
        if (!intentId.HasValue && !exchangeId.HasValue)
            throw new ArgumentException("Order requires local intent or exchange order ID.");
        LocalId = localId;
        IntentId = intentId;
        ClientLabel = clientLabel;
        ExchangeId = exchangeId;
    }
}

/// <summary>Same remote label/ID is valid in different scopes but never globally resolvable alone.</summary>
public sealed record ScopedClientOrderId
{
    public AccountKey Scope { get; }
    public ClientOrderLabel Label { get; }

    public ScopedClientOrderId(AccountKey scope, ClientOrderLabel label)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        OmsGuard.Opaque(label.Value, nameof(label));
        Label = label;
    }
}

public sealed record ScopedExchangeOrderId
{
    public AccountKey Scope { get; }
    public ExchangeOrderKey OrderId { get; }

    public ScopedExchangeOrderId(AccountKey scope, ExchangeOrderKey orderId)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        OmsGuard.Opaque(orderId.Value, nameof(orderId));
        OrderId = orderId;
    }
}

public sealed record ScopedExecutionId
{
    public AccountKey Scope { get; }
    public ExecutionNamespace Namespace { get; }
    public ExecutionKey ExecutionId { get; }

    public ScopedExecutionId(AccountKey scope, ExecutionNamespace executionNamespace, ExecutionKey executionId)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        OmsGuard.Opaque(executionNamespace.Value, nameof(executionNamespace));
        OmsGuard.Opaque(executionId.Value, nameof(executionId));
        Namespace = executionNamespace;
        ExecutionId = executionId;
    }
}

public enum OmsQuantityUnit
{
    Contracts = 1,
    BaseAsset = 2,
    QuoteAsset = 3,
    Shares = 4,
    Lots = 5
}

/// <summary>Terms are fixed at creation; replace commands create a new revision explicitly.</summary>
public sealed record OmsOrderTerms
{
    public string Instrument { get; }
    public OrderSide Side { get; }
    public OrderType Type { get; }
    public decimal RequestedQuantity { get; }
    public OmsQuantityUnit QuantityUnit { get; }

    public OmsOrderTerms(string instrument, OrderSide side, OrderType type,
        decimal requestedQuantity, OmsQuantityUnit quantityUnit)
    {
        Instrument = OmsGuard.Opaque(instrument, nameof(instrument));
        if (!Enum.IsDefined(side)) throw new ArgumentOutOfRangeException(nameof(side));
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        if (!Enum.IsDefined(quantityUnit)) throw new ArgumentOutOfRangeException(nameof(quantityUnit));
        if (requestedQuantity <= 0m) throw new ArgumentOutOfRangeException(nameof(requestedQuantity));
        Side = side;
        Type = type;
        RequestedQuantity = requestedQuantity;
        QuantityUnit = quantityUnit;
    }
}
