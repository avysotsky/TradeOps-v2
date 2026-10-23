using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TradeOps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Separate synthetic-only SQL reservation surface: DOES NOT alter legacy
/// Orders, Fills, their global indexes or execution/reconciliation runtime.
/// </summary>
public sealed class SyntheticScopedOrderReservationConfiguration
    : IEntityTypeConfiguration<SyntheticScopedOrderReservation>
{
    public void Configure(EntityTypeBuilder<SyntheticScopedOrderReservation> b)
    {
        b.ToTable("SyntheticScopedOrderReservations", table =>
        {
            table.HasCheckConstraint("CK_ScopedOrders_SyntheticOnly",
                "\"Classification\" = 'SyntheticOnly'");
            table.HasCheckConstraint("CK_ScopedOrders_ProviderInstrument",
                SyntheticReservationChecks.OrderScope);
            table.HasCheckConstraint("CK_ScopedOrders_Identity", 
                SyntheticReservationChecks.OrderText);
            table.HasCheckConstraint("CK_ScopedOrders_Terms",
                SyntheticReservationChecks.OrderTerms);
        });

        b.HasKey(x => new { x.ProviderKey, x.AccountKey, x.ClientOrderId })
            .HasName("PK_ScopedOrders");
        b.HasAlternateKey(x => new
        {
            x.ProviderKey, x.AccountKey, x.ClientOrderId,
            x.LocalOrderId, x.BrokerOrderId
        }).HasName("AK_ScopedOrders_ExecutionOwner");

        b.Property(x => x.ProviderKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.AccountKey).HasMaxLength(128).IsRequired();
        b.Property(x => x.ClientOrderId).HasMaxLength(100).IsRequired();
        b.Property(x => x.LocalOrderId).ValueGeneratedNever();
        b.Property(x => x.BrokerOrderId).HasMaxLength(100).IsRequired();
        b.Property(x => x.InstrumentKey).HasMaxLength(192).IsRequired();
        b.Property(x => x.Side).HasMaxLength(20).IsRequired();
        b.Property(x => x.OrderType).HasMaxLength(20).IsRequired();
        b.Property(x => x.RequestedQuantity).HasPrecision(28, 12);
        b.Property(x => x.Price).HasPrecision(28, 12);
        b.Property(x => x.Classification).HasMaxLength(32).IsRequired();
        b.Property(x => x.ReservedAt).IsRequired();

        b.HasIndex(x => x.LocalOrderId).IsUnique()
            .HasDatabaseName("UX_ScopedOrders_LocalOrderId");
        b.HasIndex(x => new { x.ProviderKey, x.AccountKey, x.BrokerOrderId })
            .IsUnique()
            .HasDatabaseName("UX_ScopedOrders_ProviderAccountBroker");
    }
}

/// <summary>
/// An execution can only reserve against an existing, exact synthetic
/// order/account/cOID/LocalOrderId/BrokerOrderId binding at SQL FK level.
/// </summary>
public sealed class SyntheticScopedExecutionReservationConfiguration
    : IEntityTypeConfiguration<SyntheticScopedExecutionReservation>
{
    public void Configure(EntityTypeBuilder<SyntheticScopedExecutionReservation> b)
    {
        b.ToTable("SyntheticScopedExecutionReservations", table =>
        {
            table.HasCheckConstraint("CK_ScopedExecutions_SyntheticOnly",
                "\"Classification\" = 'SyntheticOnly'");
            table.HasCheckConstraint("CK_ScopedExecutions_Identity",
                SyntheticReservationChecks.ExecutionScope);
            table.HasCheckConstraint("CK_ScopedExecutions_Terms",
                SyntheticReservationChecks.ExecutionTerms);
        });

        b.HasKey(x => new { x.ProviderKey, x.AccountKey, x.ExecutionId })
            .HasName("PK_ScopedExecutions");
        b.Property(x => x.ProviderKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.AccountKey).HasMaxLength(128).IsRequired();
        b.Property(x => x.ExecutionId).HasMaxLength(100).IsRequired();
        b.Property(x => x.ClientOrderId).HasMaxLength(100).IsRequired();
        b.Property(x => x.LocalOrderId).ValueGeneratedNever();
        b.Property(x => x.BrokerOrderId).HasMaxLength(100).IsRequired();
        b.Property(x => x.Quantity).HasPrecision(28, 12);
        b.Property(x => x.Price).HasPrecision(28, 12);
        b.Property(x => x.Fee).HasPrecision(28, 12);
        b.Property(x => x.FeeCurrency).HasMaxLength(20);
        b.Property(x => x.Classification).HasMaxLength(32).IsRequired();
        b.Property(x => x.ReservedAt).IsRequired();

        // SQL-level composite FK still exists in the additive migration.
        // Diagnostic isolation: EF relationship metadata is temporarily omitted.
    }
}
