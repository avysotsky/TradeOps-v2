using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.ScopedReservationMigrations;

[DbContext(typeof(ScopedReservationsDbContext))]
partial class ScopedReservationsDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasAnnotation("ProductVersion", "8.0.11")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

        modelBuilder.Entity("TradeOps.Infrastructure.Persistence.SyntheticScopedOrderReservation", b =>
        {
            b.Property<string>("ProviderKey").HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<string>("AccountKey").HasMaxLength(128).HasColumnType("character varying(128)");
            b.Property<string>("ClientOrderId").HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<Guid>("LocalOrderId").ValueGeneratedNever().HasColumnType("uuid");
            b.Property<string>("BrokerOrderId").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<string>("InstrumentKey").IsRequired().HasMaxLength(192).HasColumnType("character varying(192)");
            b.Property<string>("Side").IsRequired().HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<string>("OrderType").IsRequired().HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<decimal>("RequestedQuantity").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<decimal?>("Price").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<string>("Classification").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<DateTimeOffset>("ReservedAt").HasColumnType("timestamp with time zone");
            b.HasKey("ProviderKey", "AccountKey", "ClientOrderId").HasName("PK_ScopedOrders");
            b.HasAlternateKey("ProviderKey", "AccountKey", "ClientOrderId", "LocalOrderId", "BrokerOrderId").HasName("AK_ScopedOrders_ExecutionOwner");
            b.HasIndex("LocalOrderId").IsUnique().HasDatabaseName("UX_ScopedOrders_LocalOrderId");
            b.HasIndex("ProviderKey", "AccountKey", "BrokerOrderId").IsUnique().HasDatabaseName("UX_ScopedOrders_ProviderAccountBroker");
            b.ToTable("SyntheticScopedOrderReservations", t =>
            {
                t.HasCheckConstraint("CK_ScopedOrders_SyntheticOnly", "\"Classification\" = 'SyntheticOnly'");
                t.HasCheckConstraint("CK_ScopedOrders_ProviderInstrument",
                    TradeOps.Infrastructure.Persistence.Configurations.SyntheticReservationChecks.OrderScope);
                t.HasCheckConstraint("CK_ScopedOrders_Identity",
                    TradeOps.Infrastructure.Persistence.Configurations.SyntheticReservationChecks.OrderText);
                t.HasCheckConstraint("CK_ScopedOrders_Terms",
                    TradeOps.Infrastructure.Persistence.Configurations.SyntheticReservationChecks.OrderTerms);
            });
        });

        modelBuilder.Entity("TradeOps.Infrastructure.Persistence.SyntheticScopedExecutionReservation", b =>
        {
            b.Property<string>("ProviderKey").HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<string>("AccountKey").HasMaxLength(128).HasColumnType("character varying(128)");
            b.Property<string>("ExecutionId").HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<string>("ClientOrderId").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<Guid>("LocalOrderId").ValueGeneratedNever().HasColumnType("uuid");
            b.Property<string>("BrokerOrderId").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<decimal>("Quantity").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<decimal>("Price").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<decimal?>("Fee").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<string>("FeeCurrency").HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<string>("Classification").IsRequired().HasMaxLength(32).HasColumnType("character varying(32)");
            b.Property<DateTimeOffset>("ReservedAt").HasColumnType("timestamp with time zone");
            b.HasKey("ProviderKey", "AccountKey", "ExecutionId").HasName("PK_ScopedExecutions");
            b.HasIndex("ProviderKey", "AccountKey", "ClientOrderId", "LocalOrderId", "BrokerOrderId")
                .HasDatabaseName("IX_ScopedExecutions_ExactOrderBinding");
            b.ToTable("SyntheticScopedExecutionReservations", t =>
            {
                t.HasCheckConstraint("CK_ScopedExecutions_SyntheticOnly", "\"Classification\" = 'SyntheticOnly'");
                t.HasCheckConstraint("CK_ScopedExecutions_Identity",
                    TradeOps.Infrastructure.Persistence.Configurations.SyntheticReservationChecks.ExecutionScope);
                t.HasCheckConstraint("CK_ScopedExecutions_Terms",
                    TradeOps.Infrastructure.Persistence.Configurations.SyntheticReservationChecks.ExecutionTerms);
            });
        });


        modelBuilder.Entity("TradeOps.Infrastructure.Persistence.SyntheticScopedExecutionReservation", b =>
        {
            b.HasOne("TradeOps.Infrastructure.Persistence.SyntheticScopedOrderReservation", null)
                .WithMany()
                .HasForeignKey("ProviderKey", "AccountKey", "ClientOrderId", "LocalOrderId", "BrokerOrderId")
                .HasPrincipalKey("ProviderKey", "AccountKey", "ClientOrderId", "LocalOrderId", "BrokerOrderId")
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired()
                .HasConstraintName("FK_ScopedExecutions_ExactOrderBinding");
        });

#pragma warning restore 612, 618
    }
}
