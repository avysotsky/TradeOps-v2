using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence.Configurations;

public sealed class SignalIngressRequestAuditConfiguration
    : IEntityTypeConfiguration<SignalIngressRequestAudit>
{
    public void Configure(EntityTypeBuilder<SignalIngressRequestAudit> builder)
    {
        builder.ToTable("SignalIngressRequestAudits");

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Id)
            .ValueGeneratedNever();

        builder.Property(item => item.Method)
            .HasMaxLength(10);

        builder.Property(item => item.Path)
            .HasMaxLength(200);

        builder.Property(item => item.Outcome)
            .HasConversion<string>()
            .HasMaxLength(30);

        builder.Property(item => item.ClientOrderId)
            .HasMaxLength(100);

        builder.HasIndex(item => new { item.RequestId, item.ReceivedAt });
        builder.HasIndex(item => item.SignalId);
        builder.HasIndex(item => item.ClientOrderId);
    }
}
