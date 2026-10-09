using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261001083000_AddTradingSignalAudit")]
public partial class AddTradingSignalAudit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TradingSignals",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Symbol = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                Side = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                SignalType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                RequestedQuantity = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: false),
                RiskPercent = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                StopLoss = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                TakeProfit = table.Column<decimal>(type: "numeric(28,12)", precision: 28, scale: 12, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                RiskRejectionReasons = table.Column<string[]>(type: "text[]", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: true),
                ClientOrderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TradingSignals", x => x.Id);
                table.ForeignKey(
                    name: "FK_TradingSignals_Orders_OrderId",
                    column: x => x.OrderId,
                    principalTable: "Orders",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateIndex(
            name: "IX_TradingSignals_ClientOrderId",
            table: "TradingSignals",
            column: "ClientOrderId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_TradingSignals_OrderId",
            table: "TradingSignals",
            column: "OrderId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "TradingSignals");
    }
}
