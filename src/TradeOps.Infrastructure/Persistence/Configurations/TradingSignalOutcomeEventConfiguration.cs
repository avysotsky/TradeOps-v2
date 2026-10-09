using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Configurations;

public sealed class TradingSignalOutcomeEventConfiguration : IEntityTypeConfiguration<TradingSignalOutcomeEvent>
{
    public void Configure(EntityTypeBuilder<TradingSignalOutcomeEvent> builder)
    {
        builder.ToTable("TradingSignalOutcomeEvents");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.PreviousOutcome)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(item => item.Outcome)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(item => item.RiskRejectionReasons)
            .HasColumnType("text[]")
            .IsRequired();

        builder.Property(item => item.ClientOrderId)
            .HasMaxLength(100);

        builder.HasIndex(item => new { item.TradingSignalId, item.OccurredAt });

        builder.HasOne<TradingSignal>()
            .WithMany()
            .HasForeignKey(item => item.TradingSignalId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
