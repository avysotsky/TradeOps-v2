using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261001102000_AddLifecycleAndOperationalVisibility")]
public partial class AddLifecycleAndOperationalVisibility : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OperationalRunStatuses",
            columns: table => new
            {
                RunType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                IsRunning = table.Column<bool>(type: "boolean", nullable: false),
                Succeeded = table.Column<bool>(type: "boolean", nullable: true),
                OrdersScanned = table.Column<int>(type: "integer", nullable: false),
                OrdersUpdated = table.Column<int>(type: "integer", nullable: false),
                OrderIssues = table.Column<int>(type: "integer", nullable: false),
                OrdersMissingOnExchange = table.Column<int>(type: "integer", nullable: false),
                PositionsCompared = table.Column<int>(type: "integer", nullable: false),
                PositionMismatches = table.Column<int>(type: "integer", nullable: false),
                PositionSnapshots = table.Column<int>(type: "integer", nullable: false),
                ErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OperationalRunStatuses", x => x.RunType);
            });

        migrationBuilder.CreateTable(
            name: "OrderLifecycleEvents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                ClientOrderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                PreviousStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                FilledQuantity = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                AverageFillPrice = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                ExchangeOrderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrderLifecycleEvents", x => x.Id);
                table.ForeignKey(
                    name: "FK_OrderLifecycleEvents_Orders_OrderId",
                    column: x => x.OrderId,
                    principalTable: "Orders",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_OrderLifecycleEvents_ClientOrderId",
            table: "OrderLifecycleEvents",
            column: "ClientOrderId");

        migrationBuilder.CreateIndex(
            name: "IX_OrderLifecycleEvents_OrderId_OccurredAt",
            table: "OrderLifecycleEvents",
            columns: new[] { "OrderId", "OccurredAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "OperationalRunStatuses");
        migrationBuilder.DropTable(name: "OrderLifecycleEvents");
    }
}
