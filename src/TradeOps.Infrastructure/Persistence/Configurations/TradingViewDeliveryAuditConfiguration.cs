using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Configurations;

public sealed class TradingViewDeliveryAuditConfiguration
    : IEntityTypeConfiguration<TradingViewDeliveryAudit>
{
    public void Configure(
        EntityTypeBuilder<TradingViewDeliveryAudit> builder)
    {
        builder.ToTable("TradingViewDeliveryAudits");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Id)
            .ValueGeneratedNever();

        builder.Property(item => item.EventId)
            .HasMaxLength(200);

        builder.Property(item => item.Outcome)
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(item => item.ClientOrderId)
            .HasMaxLength(100);

        builder.Property(item => item.Symbol)
            .HasMaxLength(50);

        builder.Property(item => item.Action)
            .HasMaxLength(10);

        builder.HasIndex(item => new
        {
            item.EventId,
            item.ReceivedAt
        });

        builder.HasIndex(item => item.ReceivedAt);
        builder.HasIndex(item => item.SignalId);
        builder.HasIndex(item => item.ClientOrderId);
    }
}
