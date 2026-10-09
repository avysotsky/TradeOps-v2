using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Configurations;

public sealed class OperationalRunRecordConfiguration : IEntityTypeConfiguration<OperationalRunRecord>
{
    public void Configure(EntityTypeBuilder<OperationalRunRecord> builder)
    {
        builder.ToTable("OperationalRuns");
        builder.HasKey(item => item.Id);

        builder.Property(item => item.Id)
            .ValueGeneratedNever();

        builder.Property(item => item.RunType)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(item => item.ErrorMessage)
            .HasMaxLength(1000);

        builder.HasIndex(item => new { item.RunType, item.StartedAt });
    }
}
