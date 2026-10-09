using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261001155500_AddTradingSignalOutcomeHistory")]
public partial class AddTradingSignalOutcomeHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TradingSignalOutcomeEvents",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TradingSignalId = table.Column<Guid>(type: "uuid", nullable: false),
                PreviousOutcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RiskRejectionReasons = table.Column<string[]>(type: "text[]", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: true),
                ClientOrderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TradingSignalOutcomeEvents", x => x.Id);
                table.ForeignKey(
                    name: "FK_TradingSignalOutcomeEvents_TradingSignals_TradingSignalId",
                    column: x => x.TradingSignalId,
                    principalTable: "TradingSignals",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_TradingSignalOutcomeEvents_TradingSignalId_OccurredAt",
            table: "TradingSignalOutcomeEvents",
            columns: new[] { "TradingSignalId", "OccurredAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "TradingSignalOutcomeEvents");
    }
}
