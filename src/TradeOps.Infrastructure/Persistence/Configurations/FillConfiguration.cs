using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Configurations;

public sealed class FillConfiguration : IEntityTypeConfiguration<Fill>
{
    public void Configure(EntityTypeBuilder<Fill> builder)
    {
        builder.ToTable("Fills");

        builder.HasKey(fill => fill.Id);

        builder.Property(fill => fill.Id)
            .ValueGeneratedNever();

        builder.Property(fill => fill.ExchangeFillId)
            .HasMaxLength(100)
            .IsRequired();

        builder.HasIndex(fill => fill.ExchangeFillId)
            .IsUnique();

        builder.HasIndex(fill => fill.OrderId);
        builder.HasIndex(fill => fill.FilledAt);
        builder.HasIndex(fill => new { fill.OrderId, fill.FilledAt });

        builder.Property(fill => fill.Quantity)
            .HasPrecision(28, 12);

        builder.Property(fill => fill.Price)
            .HasPrecision(28, 12);

        builder.Property(fill => fill.Fee)
            .HasPrecision(28, 12);

        builder.Property(fill => fill.FeeCurrency)
            .HasMaxLength(20);

        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(fill => fill.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
