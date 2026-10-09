using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261001172500_AddSignalTransitionOccurredAtIndex")]
public partial class AddSignalTransitionOccurredAtIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_TradingSignalOutcomeEvents_OccurredAt",
            table: "TradingSignalOutcomeEvents",
            column: "OccurredAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_TradingSignalOutcomeEvents_OccurredAt",
            table: "TradingSignalOutcomeEvents");
    }
}
