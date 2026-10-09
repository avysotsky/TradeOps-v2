using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Configurations;

public sealed class OperationalRunStatusConfiguration : IEntityTypeConfiguration<OperationalRunStatus>
{
    public void Configure(EntityTypeBuilder<OperationalRunStatus> builder)
    {
        builder.ToTable("OperationalRunStatuses");
        builder.HasKey(item => item.RunType);

        builder.Property(item => item.RunType)
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(item => item.ErrorMessage)
            .HasMaxLength(1000);
    }
}
