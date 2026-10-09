using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TradeOps.Infrastructure.Persistence;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261004190000_AddTradingViewDeliveryHealthState")]
public partial class AddTradingViewDeliveryHealthState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TradingViewDeliveryHealthStates",
            columns: table => new
            {
                Id = table.Column<string>(
                    type: "character varying(50)",
                    maxLength: 50,
                    nullable: false),
                Status = table.Column<string>(
                    type: "character varying(30)",
                    maxLength: 30,
                    nullable: false),
                Reason = table.Column<string>(
                    type: "character varying(1000)",
                    maxLength: 1000,
                    nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false),
                WindowFrom = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false),
                WindowTo = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false),
                Total = table.Column<int>(
                    type: "integer",
                    nullable: false),
                Failed = table.Column<int>(
                    type: "integer",
                    nullable: false),
                Conflict = table.Column<int>(
                    type: "integer",
                    nullable: false),
                RiskRejected = table.Column<int>(
                    type: "integer",
                    nullable: false),
                AverageLatencyMilliseconds =
                    table.Column<double>(
                        type: "double precision",
                        nullable: true),
                LatestDeliveryAt = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: true),
                LatestSuccessfulAt = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_TradingViewDeliveryHealthStates",
                    x => x.Id);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "TradingViewDeliveryHealthStates");
    }
}
