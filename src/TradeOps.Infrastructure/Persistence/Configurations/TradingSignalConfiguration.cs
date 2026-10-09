using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Configurations;

public sealed class TradingSignalConfiguration : IEntityTypeConfiguration<TradingSignal>
{
    public void Configure(EntityTypeBuilder<TradingSignal> builder)
    {
        builder.ToTable("TradingSignals");

        builder.HasKey(signal => signal.Id);

        builder.Property(signal => signal.Symbol)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(signal => signal.Side)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(signal => signal.SignalType)
            .HasMaxLength(50);

        builder.Property(signal => signal.RequestedQuantity)
            .HasPrecision(28, 12);

        builder.Property(signal => signal.RiskPercent)
            .HasPrecision(18, 8);

        builder.Property(signal => signal.StopLoss)
            .HasPrecision(28, 12);

        builder.Property(signal => signal.TakeProfit)
            .HasPrecision(28, 12);

        builder.Property(signal => signal.Source)
            .HasMaxLength(100);

        builder.Property(signal => signal.Outcome)
            .HasConversion<string>()
            .HasMaxLength(20);

        builder.Property(signal => signal.RiskRejectionReasons)
            .HasColumnType("text[]")
            .IsRequired();

        builder.Property(signal => signal.ClientOrderId)
            .HasMaxLength(100);

        builder.Property(signal => signal.ExecutionIssueCode)
            .HasMaxLength(50);

        builder.Property(signal => signal.ExecutionIssueMessage)
            .HasMaxLength(1000);

        builder.HasIndex(signal => signal.ClientOrderId)
            .IsUnique();

        builder.HasIndex(signal => signal.OrderId)
            .IsUnique();

        builder.HasIndex(signal => signal.CreatedAt);
        builder.HasIndex(signal => new { signal.Symbol, signal.CreatedAt });

        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(signal => signal.OrderId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
