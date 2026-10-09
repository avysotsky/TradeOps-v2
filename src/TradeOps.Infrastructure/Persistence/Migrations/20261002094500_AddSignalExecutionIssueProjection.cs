using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261002094500_AddSignalExecutionIssueProjection")]
public partial class AddSignalExecutionIssueProjection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ExecutionIssueAt",
            table: "TradingSignals",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ExecutionIssueCode",
            table: "TradingSignals",
            type: "character varying(50)",
            maxLength: 50,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ExecutionIssueMessage",
            table: "TradingSignals",
            type: "character varying(1000)",
            maxLength: 1000,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ExecutionIssueAt", table: "TradingSignals");
        migrationBuilder.DropColumn(name: "ExecutionIssueCode", table: "TradingSignals");
        migrationBuilder.DropColumn(name: "ExecutionIssueMessage", table: "TradingSignals");
    }
}
