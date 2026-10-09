using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using TradeOps.Domain.Enums;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
partial class TradeOpsDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasAnnotation("ProductVersion", "8.0.11")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

        modelBuilder.Entity("TradeOps.Domain.Entities.Fill", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid");
            b.Property<string>("ExchangeFillId").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<decimal?>("Fee").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<string>("FeeCurrency").HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<DateTimeOffset>("FilledAt").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrderId").HasColumnType("uuid");
            b.Property<decimal>("Price").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<decimal>("Quantity").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.HasKey("Id");
            b.HasIndex("ExchangeFillId").IsUnique();
            b.HasIndex("FilledAt");
            b.HasIndex("OrderId");
            b.HasIndex("OrderId", "FilledAt");
            b.ToTable("Fills");
        });

        modelBuilder.Entity("TradeOps.Domain.Entities.OperationalRiskState", b =>
        {
            b.Property<string>("Id").ValueGeneratedNever().HasMaxLength(50).HasColumnType("character varying(50)");
            b.Property<bool>("EmergencyStop").HasColumnType("boolean");
            b.Property<string>("EmergencyStopReason").HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<bool>("TradingEnabled").HasColumnType("boolean");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.ToTable("OperationalRiskStates");
        });

        modelBuilder.Entity("TradeOps.Domain.Entities.OperationalRunRecord", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid");
            b.Property<DateTimeOffset?>("CompletedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("ErrorMessage").HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<bool>("IsRunning").HasColumnType("boolean");
            b.Property<int>("OrderIssues").HasColumnType("integer");
            b.Property<int>("OrdersMissingOnExchange").HasColumnType("integer");
            b.Property<int>("OrdersScanned").HasColumnType("integer");
            b.Property<int>("OrdersUpdated").HasColumnType("integer");
            b.Property<int>("PositionMismatches").HasColumnType("integer");
            b.Property<int>("PositionSnapshots").HasColumnType("integer");
            b.Property<int>("PositionsCompared").HasColumnType("integer");
            b.Property<string>("RunType").IsRequired().HasMaxLength(50).HasColumnType("character varying(50)");
            b.Property<DateTimeOffset>("StartedAt").HasColumnType("timestamp with time zone");
            b.Property<bool?>("Succeeded").HasColumnType("boolean");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("RunType", "StartedAt");
            b.ToTable("OperationalRuns");
        });

        modelBuilder.Entity("TradeOps.Domain.Entities.OperationalRunStatus", b =>
        {
            b.Property<string>("RunType").ValueGeneratedNever().HasMaxLength(50).HasColumnType("character varying(50)");
            b.Property<DateTimeOffset?>("CompletedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("ErrorMessage").HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<bool>("IsRunning").HasColumnType("boolean");
            b.Property<Guid?>("LatestRunId").HasColumnType("uuid");
            b.Property<int>("OrderIssues").HasColumnType("integer");
            b.Property<int>("OrdersMissingOnExchange").HasColumnType("integer");
            b.Property<int>("OrdersScanned").HasColumnType("integer");
            b.Property<int>("OrdersUpdated").HasColumnType("integer");
            b.Property<int>("PositionMismatches").HasColumnType("integer");
            b.Property<int>("PositionSnapshots").HasColumnType("integer");
            b.Property<int>("PositionsCompared").HasColumnType("integer");
            b.Property<DateTimeOffset>("StartedAt").HasColumnType("timestamp with time zone");
            b.Property<bool?>("Succeeded").HasColumnType("boolean");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("RunType");
            b.ToTable("OperationalRunStatuses");
        });

        modelBuilder.Entity("TradeOps.Domain.Entities.Order", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid");
            b.Property<decimal?>("AverageFillPrice").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<string>("ClientOrderId").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("ExchangeOrderId").HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<decimal>("FilledQuantity").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<OrderType>("OrderType").HasConversion<string>().HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<decimal?>("Price").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<decimal>("RequestedQuantity").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<OrderSide>("Side").HasConversion<string>().HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<OrderStatus>("Status").HasConversion<string>().HasMaxLength(30).HasColumnType("character varying(30)");
            b.Property<string>("Symbol").IsRequired().HasMaxLength(50).HasColumnType("character varying(50)");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("ClientOrderId").IsUnique();
            b.HasIndex("Symbol");
            b.ToTable("Orders");
        });

        modelBuilder.Entity("TradeOps.Domain.Entities.OrderLifecycleEvent", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid");
            b.Property<decimal?>("AverageFillPrice").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<string>("ClientOrderId").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<string>("ExchangeOrderId").HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<decimal>("FilledQuantity").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<DateTimeOffset>("OccurredAt").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrderId").HasColumnType("uuid");
            b.Property<OrderStatus?>("PreviousStatus").HasConversion<string>().HasMaxLength(30).HasColumnType("character varying(30)");
            b.Property<string>("Source").IsRequired().HasMaxLength(50).HasColumnType("character varying(50)");
            b.Property<OrderStatus>("Status").HasConversion<string>().HasMaxLength(30).HasColumnType("character varying(30)");
            b.HasKey("Id");
            b.HasIndex("ClientOrderId");
            b.HasIndex("OccurredAt");
            b.HasIndex("OrderId", "OccurredAt");
            b.ToTable("OrderLifecycleEvents");
        });

        modelBuilder.Entity("TradeOps.Domain.Entities.PositionSnapshot", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid");
            b.Property<decimal>("AverageEntryPrice").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<DateTimeOffset>("CapturedAt").HasColumnType("timestamp with time zone");
            b.Property<decimal?>("MarkPrice").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<decimal>("Quantity").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<decimal>("RealizedPnL").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<OrderSide?>("Side").HasConversion<string>().HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<string>("Symbol").IsRequired().HasMaxLength(50).HasColumnType("character varying(50)");
            b.Property<decimal?>("UnrealizedPnL").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.HasKey("Id");
            b.HasIndex("Symbol", "CapturedAt");
            b.ToTable("PositionSnapshots");
        });

        modelBuilder.Entity("TradeOps.Domain.Entities.RiskEvent", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<string>("EventKey").IsRequired().HasMaxLength(120).HasColumnType("character varying(120)");
            b.Property<string>("EventType").IsRequired().HasMaxLength(50).HasColumnType("character varying(50)");
            b.Property<decimal?>("ExchangeQuantity").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<OrderSide?>("ExchangeSide").HasConversion<string>().HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<DateTimeOffset>("LastObservedAt").HasColumnType("timestamp with time zone");
            b.Property<decimal?>("LocalQuantity").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<OrderSide?>("LocalSide").HasConversion<string>().HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<string>("Message").IsRequired().HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<DateTimeOffset?>("ResolvedAt").HasColumnType("timestamp with time zone");
            b.Property<RiskEventSeverity>("Severity").HasConversion<string>().HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<string>("Symbol").HasMaxLength(50).HasColumnType("character varying(50)");
            b.HasKey("Id");
            b.HasIndex("LastObservedAt");
            b.HasIndex("EventType", "EventKey").IsUnique().HasFilter("\"ResolvedAt\" IS NULL");
            b.ToTable("RiskEvents");
        });

        modelBuilder.Entity("TradeOps.Infrastructure.Persistence.SignalIngressReplayReceipt", b =>
        {
            b.Property<Guid>("RequestId").ValueGeneratedNever().HasColumnType("uuid");
            b.Property<DateTimeOffset>("ExpiresAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("ReceivedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("RequestTimestamp").HasColumnType("timestamp with time zone");
            b.HasKey("RequestId");
            b.HasIndex("ExpiresAt");
            b.ToTable("SignalIngressReplayReceipts");
        });

        modelBuilder.Entity("TradeOps.Domain.Entities.SignalIngressRequestAudit", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid");
            b.Property<string>("ClientOrderId").HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<DateTimeOffset?>("CompletedAt").HasColumnType("timestamp with time zone");
            b.Property<int?>("HttpStatusCode").HasColumnType("integer");
            b.Property<string>("Method").IsRequired().HasMaxLength(10).HasColumnType("character varying(10)");
            b.Property<Guid?>("OrderId").HasColumnType("uuid");
            b.Property<SignalIngressRequestOutcome>("Outcome").HasConversion<string>().HasMaxLength(30).HasColumnType("character varying(30)");
            b.Property<string>("Path").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<DateTimeOffset>("ReceivedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid>("RequestId").HasColumnType("uuid");
            b.Property<DateTimeOffset>("RequestTimestamp").HasColumnType("timestamp with time zone");
            b.Property<Guid?>("SignalId").HasColumnType("uuid");
            b.HasKey("Id");
            b.HasIndex("ClientOrderId");
            b.HasIndex("SignalId");
            b.HasIndex("RequestId", "ReceivedAt");
            b.ToTable("SignalIngressRequestAudits");
        });

        modelBuilder.Entity("TradeOps.Domain.Entities.TradingViewDeliveryHealthState", b =>
        {
            b.Property<string>("Id").ValueGeneratedNever().HasMaxLength(50).HasColumnType("character varying(50)");
            b.Property<double?>("AverageLatencyMilliseconds").HasColumnType("double precision");
            b.Property<int>("Conflict").HasColumnType("integer");
            b.Property<int>("Failed").HasColumnType("integer");
            b.Property<DateTimeOffset?>("LatestDeliveryAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset?>("LatestSuccessfulAt").HasColumnType("timestamp with time zone");
            b.Property<string>("Reason").IsRequired().HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<int>("RiskRejected").HasColumnType("integer");
            b.Property<TradingViewDeliveryHealthStatus>("Status").HasConversion<string>().HasMaxLength(30).HasColumnType("character varying(30)");
            b.Property<int>("Total").HasColumnType("integer");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("WindowFrom").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("WindowTo").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.ToTable("TradingViewDeliveryHealthStates");
        });

        modelBuilder.Entity("TradeOps.Domain.Entities.TradingViewDeliveryAudit", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid");
            b.Property<string>("Action").HasMaxLength(10).HasColumnType("character varying(10)");
            b.Property<string>("ClientOrderId").HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<DateTimeOffset?>("CompletedAt").HasColumnType("timestamp with time zone");
            b.Property<long?>("DurationMilliseconds").HasColumnType("bigint");
            b.Property<string>("EventId").HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<int?>("HttpStatusCode").HasColumnType("integer");
            b.Property<Guid?>("OrderId").HasColumnType("uuid");
            b.Property<TradingViewDeliveryOutcome>("Outcome").HasConversion<string>().HasMaxLength(30).HasColumnType("character varying(30)");
            b.Property<DateTimeOffset>("ReceivedAt").HasColumnType("timestamp with time zone");
            b.Property<Guid?>("SignalId").HasColumnType("uuid");
            b.Property<string>("Symbol").HasMaxLength(50).HasColumnType("character varying(50)");
            b.HasKey("Id");
            b.HasIndex("ClientOrderId");
            b.HasIndex("ReceivedAt");
            b.HasIndex("SignalId");
            b.HasIndex("EventId", "ReceivedAt");
            b.ToTable("TradingViewDeliveryAudits");
        });

        modelBuilder.Entity("TradeOps.Domain.Entities.TradingSignal", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid");
            b.Property<string>("ClientOrderId").HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset?>("ExecutionIssueAt").HasColumnType("timestamp with time zone");
            b.Property<string>("ExecutionIssueCode").HasMaxLength(50).HasColumnType("character varying(50)");
            b.Property<string>("ExecutionIssueMessage").HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<Guid?>("OrderId").HasColumnType("uuid");
            b.Property<SignalOutcome>("Outcome").HasConversion<string>().HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<decimal?>("RiskPercent").HasPrecision(18, 8).HasColumnType("numeric(18,8)");
            b.Property<string[]>("RiskRejectionReasons").IsRequired().HasColumnType("text[]");
            b.Property<decimal>("RequestedQuantity").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<OrderSide>("Side").HasConversion<string>().HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<string>("SignalType").HasMaxLength(50).HasColumnType("character varying(50)");
            b.Property<string>("Source").HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<decimal?>("StopLoss").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.Property<string>("Symbol").IsRequired().HasMaxLength(50).HasColumnType("character varying(50)");
            b.Property<decimal?>("TakeProfit").HasPrecision(28, 12).HasColumnType("numeric(28,12)");
            b.HasKey("Id");
            b.HasIndex("ClientOrderId").IsUnique();
            b.HasIndex("CreatedAt");
            b.HasIndex("OrderId").IsUnique();
            b.HasIndex("Symbol", "CreatedAt");
            b.ToTable("TradingSignals");
        });

        modelBuilder.Entity("TradeOps.Domain.Entities.TradingSignalOutcomeEvent", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedNever().HasColumnType("uuid");
            b.Property<string>("ClientOrderId").HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<DateTimeOffset>("OccurredAt").HasColumnType("timestamp with time zone");
            b.Property<Guid?>("OrderId").HasColumnType("uuid");
            b.Property<SignalOutcome>("Outcome").HasConversion<string>().HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<SignalOutcome?>("PreviousOutcome").HasConversion<string>().HasMaxLength(20).HasColumnType("character varying(20)");
            b.Property<string[]>("RiskRejectionReasons").IsRequired().HasColumnType("text[]");
            b.Property<Guid>("TradingSignalId").HasColumnType("uuid");
            b.HasKey("Id");
            b.HasIndex("TradingSignalId", "OccurredAt");
            b.ToTable("TradingSignalOutcomeEvents");
        });

        modelBuilder.Entity("TradeOps.Domain.Entities.Fill", b =>
        {
            b.HasOne("TradeOps.Domain.Entities.Order", null)
                .WithMany()
                .HasForeignKey("OrderId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        modelBuilder.Entity("TradeOps.Domain.Entities.OrderLifecycleEvent", b =>
        {
            b.HasOne("TradeOps.Domain.Entities.Order", null)
                .WithMany()
                .HasForeignKey("OrderId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        modelBuilder.Entity("TradeOps.Domain.Entities.TradingSignal", b =>
        {
            b.HasOne("TradeOps.Domain.Entities.Order", null)
                .WithMany()
                .HasForeignKey("OrderId")
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity("TradeOps.Domain.Entities.TradingSignalOutcomeEvent", b =>
        {
            b.HasOne("TradeOps.Domain.Entities.TradingSignal", null)
                .WithMany()
                .HasForeignKey("TradingSignalId")
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
        });
#pragma warning restore 612, 618
    }
}
