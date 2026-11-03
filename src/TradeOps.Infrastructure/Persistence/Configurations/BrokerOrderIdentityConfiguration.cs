using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Synthetic-only broker scope. The nullable historical Orders and Fills
/// representations are NOT touched. Untrusted legacy orders have zero
/// corresponding rows and therefore never qualify for scoped shadow reads.
/// </summary>
public sealed class BrokerOrderIdentityConfiguration
    : IEntityTypeConfiguration<BrokerOrderIdentity>
{
    public void Configure(EntityTypeBuilder<BrokerOrderIdentity> builder)
    {
        builder.ToTable("BrokerOrderIdentities", table =>
        {
            table.HasCheckConstraint("CK_BrokerOrderIdentities_SyntheticOnly",
                "\"Classification\" = 'SyntheticOnly'");
            table.HasCheckConstraint("CK_BrokerOrderIdentities_QualifiedSyntheticInstrument",
                "(\"ProviderKey\" = 'Mock' AND left(\"InstrumentKey\", 5) = 'MOCK:' " +
                "AND length(\"InstrumentKey\") > 5) OR " +
                "(\"ProviderKey\" = 'IbkrOffline' " +
                "AND left(\"InstrumentKey\", 22) = 'SYNTHETIC-IBKR-SYMBOL:' " +
                "AND length(\"InstrumentKey\") > 22)");
            table.HasCheckConstraint("CK_BrokerOrderIdentities_NonBlankScope",
                "length(btrim(\"AccountKey\")) > 0 " +
                "AND \"AccountKey\" = btrim(\"AccountKey\") " +
                "AND length(btrim(\"ClientOrderId\")) > 0 " +
                "AND \"ClientOrderId\" = btrim(\"ClientOrderId\") " +
                "AND (\"BrokerOrderId\" IS NULL OR " +
                "(length(btrim(\"BrokerOrderId\")) > 0 " +
                "AND \"BrokerOrderId\" = btrim(\"BrokerOrderId\")))");
        });

        builder.HasKey(identity => identity.OrderId);
        builder.Property(identity => identity.OrderId)
            .ValueGeneratedNever();

        builder.Property(identity => identity.ProviderKey)
            .HasMaxLength(64).IsRequired();
        builder.Property(identity => identity.AccountKey)
            .HasMaxLength(128).IsRequired();
        builder.Property(identity => identity.InstrumentKey)
            .HasMaxLength(192).IsRequired();
        builder.Property(identity => identity.ClientOrderId)
            .HasMaxLength(100).IsRequired();
        builder.Property(identity => identity.BrokerOrderId)
            .HasMaxLength(100);

        builder.Property(identity => identity.Classification)
            .HasMaxLength(32).IsRequired();

        builder.Property(identity => identity.RecordedAt)
            .IsRequired();

        builder.HasIndex(identity => new
        {
            identity.ProviderKey,
            identity.AccountKey,
            identity.ClientOrderId
        }).IsUnique().HasDatabaseName("UX_BrokerOrderIdentities_ProviderAccountClient");

        builder.HasIndex(identity => new
        {
            identity.ProviderKey,
            identity.AccountKey,
            identity.BrokerOrderId
        }).IsUnique()
            .HasFilter("\"BrokerOrderId\" IS NOT NULL")
            .HasDatabaseName("UX_BrokerOrderIdentities_ProviderAccountBroker");

        builder.HasOne<Order>()
            .WithOne()
            .HasForeignKey<BrokerOrderIdentity>(identity => identity.OrderId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
