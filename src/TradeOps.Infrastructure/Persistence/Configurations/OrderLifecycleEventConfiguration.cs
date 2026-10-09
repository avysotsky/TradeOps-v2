using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Configurations;

public sealed class OrderLifecycleEventConfiguration : IEntityTypeConfiguration<OrderLifecycleEvent>
{
    public void Configure(EntityTypeBuilder<OrderLifecycleEvent> builder)
    {
        builder.ToTable("OrderLifecycleEvents");
        builder.HasKey(item => item.Id);

        builder.Property(item => item.ClientOrderId)
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(item => item.PreviousStatus)
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(item => item.Status)
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(item => item.FilledQuantity)
            .HasPrecision(28, 12);

        builder.Property(item => item.AverageFillPrice)
            .HasPrecision(28, 12);

        builder.Property(item => item.ExchangeOrderId)
            .HasMaxLength(100);

        builder.Property(item => item.Source)
            .HasMaxLength(50)
            .IsRequired();

        builder.HasIndex(item => new { item.OrderId, item.OccurredAt });
        builder.HasIndex(item => item.ClientOrderId);
        builder.HasIndex(item => item.OccurredAt);

        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(item => item.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
