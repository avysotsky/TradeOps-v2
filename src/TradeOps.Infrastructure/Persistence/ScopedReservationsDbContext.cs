using Microsoft.EntityFrameworkCore;
using TradeOps.Infrastructure.Persistence.Configurations;

namespace TradeOps.Infrastructure.Persistence;

/// <summary>
/// Dedicated opt-in synthetic reservation context. The live TradeOps
/// database model and its __EFMigrationsHistory migrations do not
/// include these entities. This context is constructed ONLY by the
/// explicitly authorized disposable PostgreSQL smoke executable.
/// </summary>
public sealed class ScopedReservationsDbContext(
    DbContextOptions<ScopedReservationsDbContext> options)
    : DbContext(options)
{
    public DbSet<SyntheticScopedOrderReservation> SyntheticScopedOrderReservations =>
        Set<SyntheticScopedOrderReservation>();

    public DbSet<SyntheticScopedExecutionReservation> SyntheticScopedExecutionReservations =>
        Set<SyntheticScopedExecutionReservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new SyntheticScopedOrderReservationConfiguration());
        modelBuilder.ApplyConfiguration(new SyntheticScopedExecutionReservationConfiguration());
    }
}
