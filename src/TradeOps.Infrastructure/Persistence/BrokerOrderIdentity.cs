namespace TradeOps.Infrastructure.Persistence;

/// <summary>
/// Additive account/provider/instrument identity bound to an existing local
/// Order by immutable OrderId. This initial stage is SYNTHETIC ONLY.
/// The legacy order's Symbol and external order ID cannot prove a broker,
/// an account or a qualified instrument.
/// </summary>
public sealed class BrokerOrderIdentity
{
    public const string SyntheticClassification = "SyntheticOnly";

    public Guid OrderId { get; set; }

    public required string ProviderKey { get; set; }

    public required string AccountKey { get; set; }

    public required string InstrumentKey { get; set; }

    public required string ClientOrderId { get; set; }

    public string? BrokerOrderId { get; set; }

    // Database CHECK constraints prohibit broker-verified classifications
    // until a separate authenticated provenance design is approved.
    public string Classification { get; set; } = SyntheticClassification;

    public DateTimeOffset RecordedAt { get; set; }
}
