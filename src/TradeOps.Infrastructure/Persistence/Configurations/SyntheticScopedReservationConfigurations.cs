using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TradeOps.Infrastructure.Persistence.Configurations;

// DIAGNOSTIC ONLY: minimal mapping isolates EF constraint metadata from
// the legacy suite. Exact schema constraints remain recoverable via git.
public sealed class SyntheticScopedOrderReservationConfiguration
    : IEntityTypeConfiguration<SyntheticScopedOrderReservation>
{
    public void Configure(EntityTypeBuilder<SyntheticScopedOrderReservation> b)
    {
        b.ToTable("SyntheticScopedOrderReservations");
        b.HasKey(x => new { x.ProviderKey, x.AccountKey, x.ClientOrderId });
    }
}

public sealed class SyntheticScopedExecutionReservationConfiguration
    : IEntityTypeConfiguration<SyntheticScopedExecutionReservation>
{
    public void Configure(EntityTypeBuilder<SyntheticScopedExecutionReservation> b)
    {
        b.ToTable("SyntheticScopedExecutionReservations");
        b.HasKey(x => new { x.ProviderKey, x.AccountKey, x.ExecutionId });
    }
}
