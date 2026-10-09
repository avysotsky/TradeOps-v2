using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Configurations;

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");

        builder.HasKey(order => order.Id);

        builder.Property(order => order.ClientOrderId)
            .HasMaxLength(100)
            .IsRequired();

        builder.HasIndex(order => order.ClientOrderId)
            .IsUnique();

        builder.Property(order => order.ExchangeOrderId)
            .HasMaxLength(100);

        builder.Property(order => order.Symbol)
            .HasMaxLength(50)
            .IsRequired();

        builder.HasIndex(order => order.Symbol);

        builder.Property(order => order.Side)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(order => order.OrderType)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(order => order.Status)
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(order => order.RequestedQuantity)
            .HasPrecision(28, 12);

        builder.Property(order => order.FilledQuantity)
            .HasPrecision(28, 12);

        builder.Property(order => order.AverageFillPrice)
            .HasPrecision(28, 12);

        builder.Property(order => order.Price)
            .HasPrecision(28, 12);
    }
}
