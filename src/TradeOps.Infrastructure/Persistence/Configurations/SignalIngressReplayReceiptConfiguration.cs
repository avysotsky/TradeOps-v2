using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TradeOps.Infrastructure.Persistence.Configurations;

public sealed class SignalIngressReplayReceiptConfiguration
    : IEntityTypeConfiguration<SignalIngressReplayReceipt>
{
    public void Configure(EntityTypeBuilder<SignalIngressReplayReceipt> builder)
    {
        builder.ToTable("SignalIngressReplayReceipts");

        builder.HasKey(receipt => receipt.RequestId);

        builder.Property(receipt => receipt.RequestId)
            .ValueGeneratedNever();

        builder.HasIndex(receipt => receipt.ExpiresAt);
    }
}
