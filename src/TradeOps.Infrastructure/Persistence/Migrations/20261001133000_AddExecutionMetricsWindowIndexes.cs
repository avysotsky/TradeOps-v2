using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261001133000_AddExecutionMetricsWindowIndexes")]
public partial class AddExecutionMetricsWindowIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_Fills_FilledAt",
            table: "Fills",
            column: "FilledAt");

        migrationBuilder.CreateIndex(
            name: "IX_OrderLifecycleEvents_OccurredAt",
            table: "OrderLifecycleEvents",
            column: "OccurredAt");

        migrationBuilder.CreateIndex(
            name: "IX_TradingSignals_CreatedAt",
            table: "TradingSignals",
            column: "CreatedAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Fills_FilledAt",
            table: "Fills");

        migrationBuilder.DropIndex(
            name: "IX_OrderLifecycleEvents_OccurredAt",
            table: "OrderLifecycleEvents");

        migrationBuilder.DropIndex(
            name: "IX_TradingSignals_CreatedAt",
            table: "TradingSignals");
    }
}
