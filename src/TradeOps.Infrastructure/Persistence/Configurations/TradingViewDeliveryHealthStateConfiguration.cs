using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Configurations;

public sealed class TradingViewDeliveryHealthStateConfiguration
    : IEntityTypeConfiguration<TradingViewDeliveryHealthState>
{
    public void Configure(
        EntityTypeBuilder<TradingViewDeliveryHealthState> builder)
    {
        builder.ToTable("TradingViewDeliveryHealthStates");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Id)
            .HasMaxLength(50)
            .ValueGeneratedNever();

        builder.Property(item => item.Status)
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(item => item.Reason)
            .HasMaxLength(1000);
    }
}
