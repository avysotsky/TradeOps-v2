using Microsoft.EntityFrameworkCore;
using TradeOps.Domain.Entities;

namespace TradeOps.Infrastructure.Persistence;

public sealed class TradeOpsDbContext(DbContextOptions<TradeOpsDbContext> options)
    : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLifecycleEvent> OrderLifecycleEvents => Set<OrderLifecycleEvent>();

    public DbSet<OperationalRunStatus> OperationalRunStatuses => Set<OperationalRunStatus>();

    public DbSet<OperationalRunRecord> OperationalRuns => Set<OperationalRunRecord>();

    public DbSet<Fill> Fills => Set<Fill>();

    public DbSet<PositionSnapshot> PositionSnapshots => Set<PositionSnapshot>();

    public DbSet<RiskEvent> RiskEvents => Set<RiskEvent>();

    public DbSet<OperationalRiskState> OperationalRiskStates => Set<OperationalRiskState>();

    public DbSet<TradingSignal> TradingSignals => Set<TradingSignal>();

    public DbSet<TradingSignalOutcomeEvent> TradingSignalOutcomeEvents => Set<TradingSignalOutcomeEvent>();

    public DbSet<SignalIngressReplayReceipt> SignalIngressReplayReceipts =>
        Set<SignalIngressReplayReceipt>();

    public DbSet<SignalIngressRequestAudit> SignalIngressRequestAudits =>
        Set<SignalIngressRequestAudit>();

    public DbSet<TradingViewDeliveryAudit> TradingViewDeliveryAudits =>
        Set<TradingViewDeliveryAudit>();

    public DbSet<TradingViewDeliveryHealthState> TradingViewDeliveryHealthStates =>
        Set<TradingViewDeliveryHealthState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TradeOpsDbContext).Assembly);
    }
}
