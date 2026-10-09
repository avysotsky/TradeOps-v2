using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Configurations;

public sealed class RiskEventConfiguration : IEntityTypeConfiguration<RiskEvent>
{
    public void Configure(EntityTypeBuilder<RiskEvent> builder)
    {
        builder.ToTable("RiskEvents");

        builder.HasKey(riskEvent => riskEvent.Id);

        builder.Property(riskEvent => riskEvent.Id)
            .ValueGeneratedNever();

        builder.Property(riskEvent => riskEvent.EventType)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(riskEvent => riskEvent.EventKey)
            .HasMaxLength(120)
            .IsRequired();

        builder.Property(riskEvent => riskEvent.Symbol)
            .HasMaxLength(50);

        builder.Property(riskEvent => riskEvent.Message)
            .HasMaxLength(1000)
            .IsRequired();

        builder.Property(riskEvent => riskEvent.Severity)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(riskEvent => riskEvent.LocalSide)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(riskEvent => riskEvent.ExchangeSide)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(riskEvent => riskEvent.LocalQuantity)
            .HasPrecision(28, 12);

        builder.Property(riskEvent => riskEvent.ExchangeQuantity)
            .HasPrecision(28, 12);

        builder.HasIndex(riskEvent => new { riskEvent.EventType, riskEvent.EventKey })
            .IsUnique()
            .HasFilter("\"ResolvedAt\" IS NULL");

        builder.HasIndex(riskEvent => riskEvent.LastObservedAt);
    }
}
