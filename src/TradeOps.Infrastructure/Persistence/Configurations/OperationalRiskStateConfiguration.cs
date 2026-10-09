using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Configurations;

public sealed class OperationalRiskStateConfiguration : IEntityTypeConfiguration<OperationalRiskState>
{
    public void Configure(EntityTypeBuilder<OperationalRiskState> builder)
    {
        builder.ToTable("OperationalRiskStates");

        builder.HasKey(state => state.Id);

        builder.Property(state => state.Id)
            .HasMaxLength(50)
            .ValueGeneratedNever();

        builder.Property(state => state.EmergencyStopReason)
            .HasMaxLength(500);
    }
}
