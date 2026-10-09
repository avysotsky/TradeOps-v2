using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Configurations;

public sealed class PositionSnapshotConfiguration : IEntityTypeConfiguration<PositionSnapshot>
{
    public void Configure(EntityTypeBuilder<PositionSnapshot> builder)
    {
        builder.ToTable("PositionSnapshots");

        builder.HasKey(snapshot => snapshot.Id);

        builder.Property(snapshot => snapshot.Id)
            .ValueGeneratedNever();

        builder.Property(snapshot => snapshot.Symbol)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(snapshot => snapshot.Side)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(snapshot => snapshot.Quantity)
            .HasPrecision(28, 12);

        builder.Property(snapshot => snapshot.AverageEntryPrice)
            .HasPrecision(28, 12);

        builder.Property(snapshot => snapshot.RealizedPnL)
            .HasPrecision(28, 12);

        builder.Property(snapshot => snapshot.MarkPrice)
            .HasPrecision(28, 12);

        builder.Property(snapshot => snapshot.UnrealizedPnL)
            .HasPrecision(28, 12);

        builder.HasIndex(snapshot => new { snapshot.Symbol, snapshot.CapturedAt });
    }
}
