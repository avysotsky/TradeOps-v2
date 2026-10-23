using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using TradeOps.Infrastructure.Persistence;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.ScopedReservationMigrations;

// Frozen historic migration snapshot, expressed without IEntityTypeConfiguration
// classes. Otherwise ApplyConfigurationsFromAssembly in the LIVE DbContext
// would accidentally discover extra synthetic reservation configurations.
[DbContext(typeof(ScopedReservationsDbContext))]
partial class ScopedReservationsDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasAnnotation("ProductVersion", "10.0.12")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

        modelBuilder.Entity<SyntheticScopedOrderReservation>(b =>
        {
            b.ToTable("SyntheticScopedOrderReservations", table =>
                    {
                        table.HasCheckConstraint("CK_ScopedOrders_SyntheticOnly",
                            "\"Classification\" = 'SyntheticOnly'");
                        table.HasCheckConstraint("CK_ScopedOrders_ProviderInstrument",
                            FrozenReservationChecks.OrderScope);
                        table.HasCheckConstraint("CK_ScopedOrders_Identity", 
                            FrozenReservationChecks.OrderText);
                        table.HasCheckConstraint("CK_ScopedOrders_Terms",
                            FrozenReservationChecks.OrderTerms);
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
        });

        modelBuilder.Entity<SyntheticScopedExecutionReservation>(b =>
        {
            b.ToTable("SyntheticScopedExecutionReservations", table =>
                    {
                        table.HasCheckConstraint("CK_ScopedExecutions_SyntheticOnly",
                            "\"Classification\" = 'SyntheticOnly'");
                        table.HasCheckConstraint("CK_ScopedExecutions_Identity",
                            FrozenReservationChecks.ExecutionScope);
                        table.HasCheckConstraint("CK_ScopedExecutions_Terms",
                            FrozenReservationChecks.ExecutionTerms);
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
            
                    b.HasOne<SyntheticScopedOrderReservation>()
                        .WithMany()
                        .HasForeignKey(x => new
                        {
                            x.ProviderKey, x.AccountKey, x.ClientOrderId,
                            x.LocalOrderId, x.BrokerOrderId
                        })
                        .HasPrincipalKey(x => new
                        {
                            x.ProviderKey, x.AccountKey, x.ClientOrderId,
                            x.LocalOrderId, x.BrokerOrderId
                        })
                        .OnDelete(DeleteBehavior.Restrict)
                        .HasConstraintName("FK_ScopedExecutions_ExactOrderBinding");
        });
    }
}


/// <summary>
/// Fixed SQL fragments used by TO-V2-21's model and its initial migration.
/// Treat the values as an immutable historical migration contract.
/// </summary>
internal static class FrozenReservationChecks
{
    internal const string OrderScope =
        "(\"ProviderKey\" = 'Mock' " +
        "AND left(\"InstrumentKey\", 5) = 'MOCK:' AND length(\"InstrumentKey\") > 5) " +
        "OR (\"ProviderKey\" = 'IbkrOffline' " +
        "AND left(\"InstrumentKey\", 22) = 'SYNTHETIC-IBKR-SYMBOL:' " +
        "AND length(\"InstrumentKey\") > 22)";

    internal const string OrderText =
        "length(btrim(\"AccountKey\")) > 0 AND \"AccountKey\" = btrim(\"AccountKey\") " +
        "AND length(btrim(\"ClientOrderId\")) > 0 AND \"ClientOrderId\" = btrim(\"ClientOrderId\") " +
        "AND length(btrim(\"BrokerOrderId\")) > 0 AND \"BrokerOrderId\" = btrim(\"BrokerOrderId\") " +
        "AND length(btrim(\"InstrumentKey\")) > 0 AND \"InstrumentKey\" = btrim(\"InstrumentKey\") " +
        "AND \"LocalOrderId\" <> '00000000-0000-0000-0000-000000000000'::uuid";

    internal const string OrderTerms =
        "\"Side\" IN ('Buy','Sell') " +
        "AND \"OrderType\" IN ('Market','Limit','Stop') " +
        "AND \"RequestedQuantity\" > 0 AND \"RequestedQuantity\" < 10000000000000000 " +
        "AND (\"Price\" IS NULL OR (\"Price\" > 0 AND \"Price\" < 10000000000000000))";

    internal const string ExecutionScope =
        "\"ProviderKey\" IN ('Mock','IbkrOffline') " +
        "AND length(btrim(\"AccountKey\")) > 0 " +
        "AND \"AccountKey\" = btrim(\"AccountKey\") " +
        "AND length(btrim(\"ClientOrderId\")) > 0 " +
        "AND \"ClientOrderId\" = btrim(\"ClientOrderId\") " +
        "AND length(btrim(\"ExecutionId\")) > 0 " +
        "AND \"ExecutionId\" = btrim(\"ExecutionId\") " +
        "AND length(btrim(\"BrokerOrderId\")) > 0 " +
        "AND \"BrokerOrderId\" = btrim(\"BrokerOrderId\") " +
        "AND \"LocalOrderId\" <> '00000000-0000-0000-0000-000000000000'::uuid";

    internal const string ExecutionTerms =
        "\"Quantity\" > 0 AND \"Quantity\" < 10000000000000000 " +
        "AND \"Price\" > 0 AND \"Price\" < 10000000000000000 " +
        "AND (\"Fee\" IS NULL OR (\"Fee\" >= 0 AND \"Fee\" < 10000000000000000))";
}

