using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261001144000_AddSymbolScopedExecutionMetricsIndexes")]
public partial class AddSymbolScopedExecutionMetricsIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_Fills_OrderId_FilledAt",
            table: "Fills",
            columns: new[] { "OrderId", "FilledAt" });

        migrationBuilder.CreateIndex(
            name: "IX_Orders_Symbol",
            table: "Orders",
            column: "Symbol");

        migrationBuilder.CreateIndex(
            name: "IX_TradingSignals_Symbol_CreatedAt",
            table: "TradingSignals",
            columns: new[] { "Symbol", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Fills_OrderId_FilledAt",
            table: "Fills");

        migrationBuilder.DropIndex(
            name: "IX_Orders_Symbol",
            table: "Orders");

        migrationBuilder.DropIndex(
            name: "IX_TradingSignals_Symbol_CreatedAt",
            table: "TradingSignals");
    }
}
