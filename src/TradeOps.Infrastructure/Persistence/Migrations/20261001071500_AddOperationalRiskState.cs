using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeOps.Infrastructure.Persistence.Migrations;

[DbContext(typeof(TradeOpsDbContext))]
[Migration("20261001071500_AddOperationalRiskState")]
public partial class AddOperationalRiskState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OperationalRiskStates",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                TradingEnabled = table.Column<bool>(type: "boolean", nullable: false),
                EmergencyStop = table.Column<bool>(type: "boolean", nullable: false),
                EmergencyStopReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OperationalRiskStates", x => x.Id);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "OperationalRiskStates");
    }
}
